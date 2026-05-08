WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

builder.Logging.ClearProviders();
builder.Services.AddHttpForwarder();
builder.WebHost.UseKestrelCore();
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(9999, listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);
});

WebApplication app = builder.Build();

ProxyTarget[] targets =
[
    new("/sockets/api1.sock"),
    new("/sockets/api2.sock")
];

ForwarderRequestConfig requestConfig = new()
{
    ActivityTimeout = TimeSpan.FromSeconds(5)
};

RoundRobinPicker picker = new(targets);

app.Map("/{**catchAll}", async (HttpContext context, IHttpForwarder forwarder) =>
{
    ProxyTarget target = picker.Next();
    ForwarderError error = await forwarder.SendAsync(context, target.DestinationPrefix, target.HttpClient, requestConfig, HttpTransformer.Default, context.RequestAborted);
    if (error != ForwarderError.None && !context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
    }
});

app.Run();

/// <summary>
/// Single upstream API target backed by a Unix Domain Socket and one reusable YARP forwarder client.
/// </summary>
internal sealed class ProxyTarget
{
    private readonly string socketPath;

    /// <summary>
    /// Creates a pooled HTTP/1 forwarder client for one Unix Domain Socket.
    /// </summary>
    /// <param name="socketPath">Absolute Unix Domain Socket path exposed by the API container.</param>
    public ProxyTarget(string socketPath)
    {
        this.socketPath = socketPath;
        DestinationPrefix = $"http://{Path.GetFileNameWithoutExtension(socketPath)}/";
        HttpClient = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator(),
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            ConnectCallback = ConnectAsync
        });
    }

    /// <summary>URI prefix used by YARP to rebuild forwarded request URIs.</summary>
    public string DestinationPrefix { get; }

    /// <summary>Reusable low-level HTTP invoker. YARP recommends this over <see cref="HttpClient"/> for proxying.</summary>
    public HttpMessageInvoker HttpClient { get; }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext _, CancellationToken cancellationToken)
    {
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Lock-free round-robin target picker used by the YARP experiment.
/// </summary>
internal sealed class RoundRobinPicker
{
    private readonly ProxyTarget[] targets;
    private int cursor = -1;

    /// <summary>
    /// Creates a picker over fixed upstream targets.
    /// </summary>
    /// <param name="targets">Non-empty target array.</param>
    public RoundRobinPicker(ProxyTarget[] targets)
    {
        this.targets = targets;
    }

    /// <summary>
    /// Returns next target using an atomic increment. Overflow is harmless because modulo keeps index in range.
    /// </summary>
    /// <returns>Selected upstream target.</returns>
    public ProxyTarget Next()
    {
        uint next = (uint)Interlocked.Increment(ref cursor);
        return targets[next % (uint)targets.Length];
    }
}
