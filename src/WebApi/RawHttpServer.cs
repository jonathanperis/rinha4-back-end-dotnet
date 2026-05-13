/// <summary>
/// Raw HTTP/1 server used for the competition container. It binds to the Unix
/// domain socket used by nginx in Docker, or TCP port 8080 for local runs, then
/// keeps request handling allocation-light with one pooled buffer per connection.
/// </summary>
internal sealed class RawHttpServer
{
    private const int ListenBacklog = 16384;
    private const int DefaultAcceptLoops = 1;
    private const int ConnectionBufferBytes = 4096;
    private const int MaxBodyBytes = 4096;

    private readonly string? socketPath;
    private readonly FraudScorer scorer;
    private readonly int keepAliveMax;

    /// <summary>
    /// Creates a server bound to the configured socket path and fraud scorer.
    /// The scorer is shared by all connections because its dataset is immutable.
    /// </summary>
    public RawHttpServer(string? socketPath, FraudScorer scorer)
    {
        this.socketPath = socketPath;
        this.scorer = scorer;
        keepAliveMax = GetNonNegativeIntEnvironment("KEEP_ALIVE_MAX", 0);
    }

    /// <summary>
    /// Starts listening and runs the configured number of accept loops forever.
    /// Unix socket permissions are widened on Linux so nginx can connect as its user.
    /// </summary>
    public async Task RunAsync()
    {
        using var listener = CreateListener();
        listener.Listen(ListenBacklog);

        if (!string.IsNullOrEmpty(socketPath) && OperatingSystem.IsLinux())
            AllowSocketClients(socketPath);

        Console.WriteLine(string.IsNullOrEmpty(socketPath)
            ? "Raw HTTP/1 server listening on 0.0.0.0:8080"
            : $"Raw HTTP/1 server listening on {socketPath}");

        int acceptLoopCount = GetPositiveIntEnvironment("ACCEPT_LOOPS", DefaultAcceptLoops);
        var acceptLoops = new Task[acceptLoopCount];
        for (int i = 0; i < acceptLoops.Length; i++)
            acceptLoops[i] = AcceptLoopAsync(listener);

        await Task.WhenAll(acceptLoops);
    }

    /// <summary>
    /// Creates and binds the listening socket for Docker Unix sockets or local TCP.
    /// </summary>
    /// <returns>A bound socket ready for <see cref="Socket.Listen(int)"/>.</returns>
    private Socket CreateListener()
    {
        if (string.IsNullOrEmpty(socketPath))
        {
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Any, 8080));
            return listener;
        }

        if (File.Exists(socketPath))
            File.Delete(socketPath);

        var unixListener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        unixListener.Bind(new UnixDomainSocketEndPoint(socketPath));
        return unixListener;
    }

    /// <summary>
    /// Accepts client sockets and dispatches each connection to its own async loop.
    /// The method intentionally does not await connection tasks so accept latency stays low.
    /// </summary>
    private async Task AcceptLoopAsync(Socket listener)
    {
        while (true)
        {
            Socket client = await listener.AcceptAsync();
            ThreadPool.UnsafeQueueUserWorkItem(
                static state => state.Server.HandleConnection(state.Socket),
                (Server: this, Socket: client),
                preferLocal: false);
        }
    }

    /// <summary>
    /// Reads a positive integer environment variable or returns a fallback.
    /// Invalid, missing, zero, and negative values are ignored for predictable startup.
    /// </summary>
    private static int GetPositiveIntEnvironment(string name, int fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : fallback;
    }

    private static int GetNonNegativeIntEnvironment(string name, int fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out int parsed) && parsed >= 0 ? parsed : fallback;
    }

    /// <summary>
    /// Handles all keep-alive requests for one socket using a pooled read buffer.
    /// It accepts bodies up to the expected Rinha payload size and preserves pipelined bytes.
    /// </summary>
    private void HandleConnection(Socket socket)
    {
        using (socket)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(ConnectionBufferBytes);
            int start = 0;
            int end = 0;
            int requests = 0;

            try
            {
                while (true)
                {
                    int headerEnd;
                    while ((headerEnd = HttpWire.FindHeaderEnd(buffer, start, end)) < 0)
                    {
                        if (end == buffer.Length)
                        {
                            HttpWire.SendAll(socket, HttpResponses.BadRequest.Span);
                            return;
                        }

                        int read = socket.Receive(buffer.AsSpan(end), SocketFlags.None);
                        if (read == 0)
                            return;

                        end += read;
                    }

                    int contentLength = HttpWire.GetContentLength(buffer.AsSpan(start, headerEnd - start));
                    if (contentLength < 0 || contentLength > MaxBodyBytes)
                    {
                        HttpWire.SendAll(socket, HttpResponses.BadRequest.Span);
                        return;
                    }

                    int bodyStart = headerEnd + 4;
                    int requestEnd = bodyStart + contentLength;
                    while (end < requestEnd)
                    {
                        if (end == buffer.Length)
                        {
                            HttpWire.SendAll(socket, HttpResponses.BadRequest.Span);
                            return;
                        }

                        int read = socket.Receive(buffer.AsSpan(end), SocketFlags.None);
                        if (read == 0)
                            return;

                        end += read;
                    }

                    ReadOnlyMemory<byte> response = SelectResponse(buffer.AsSpan(start, headerEnd - start), buffer.AsSpan(bodyStart, contentLength));
                    HttpWire.SendAll(socket, response.Span);
                    requests++;

                    int remaining = end - requestEnd;
                    if (remaining > 0)
                        Buffer.BlockCopy(buffer, requestEnd, buffer, 0, remaining);

                    start = 0;
                    end = remaining;
                    if (keepAliveMax > 0 && requests >= keepAliveMax)
                        return;
                }
            }
            catch
            {
                // Client resets are expected under load; close connection without logging.
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// Routes the request line and body to the readiness, fraud-score, or not-found response.
    /// This only inspects byte spans from the pooled buffer and avoids route-table allocations.
    /// </summary>
    private ReadOnlyMemory<byte> SelectResponse(ReadOnlySpan<byte> header, ReadOnlySpan<byte> body)
    {
        if (HttpWire.IsPath(header, "GET "u8, "/ready"u8))
            return HttpResponses.Ready;

        if (HttpWire.IsPath(header, "POST "u8, "/fraud-score"u8))
            return body.IsEmpty ? HttpResponses.BadRequest : scorer.ScoreFraudRequest(body);

        return HttpResponses.NotFound;
    }

    /// <summary>
    /// Opens the Unix domain socket file permissions so the reverse proxy process can connect.
    /// This is Linux-only because UnixFileMode is only meaningful inside the competition container.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private static void AllowSocketClients(string socketPath)
    {
        const UnixFileMode Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                  UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                  UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

#pragma warning disable CA1416
        File.SetUnixFileMode(socketPath, Mode);
#pragma warning restore CA1416
    }
}
