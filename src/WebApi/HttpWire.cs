/// <summary>
/// Low-level HTTP/1 byte helpers for the raw socket server. These methods keep
/// parsing intentionally narrow to the Rinha endpoint contract and avoid string
/// allocation while routing and reading Content-Length.
/// </summary>
internal static class HttpWire
{
    /// <summary>
    /// Sends the full response buffer, retrying after partial socket writes.
    /// A non-positive send ends the loop because the peer can no longer receive data.
    /// </summary>
    public static async ValueTask SendAllAsync(Socket socket, ReadOnlyMemory<byte> data)
    {
        while (!data.IsEmpty)
        {
            int sent = await socket.SendAsync(data, SocketFlags.None);
            if (sent <= 0)
                return;

            data = data.Slice(sent);
        }
    }

    /// <summary>
    /// Sends the full response buffer on the synchronous raw-server path.
    /// </summary>
    public static void SendAll(Socket socket, ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            int sent = socket.Send(data, SocketFlags.None);
            if (sent <= 0)
                return;

            data = data[sent..];
        }
    }

    /// <summary>
    /// Finds the CRLFCRLF delimiter that terminates an HTTP header block within a buffer window.
    /// Returns -1 when the current window still needs more bytes from the socket.
    /// </summary>
    public static int FindHeaderEnd(ReadOnlySpan<byte> buffer, int start, int end)
    {
        int relative = buffer.Slice(start, end - start).IndexOf("\r\n\r\n"u8);
        return relative < 0 ? -1 : start + relative;
    }

    /// <summary>
    /// Reads Content-Length from an ASCII HTTP header block. Missing length maps
    /// to zero, while malformed digits map to -1 so callers can reject the request.
    /// </summary>
    public static int GetContentLength(ReadOnlySpan<byte> header)
    {
        int fast = header.IndexOf("Content-Length: "u8);
        if (fast >= 0)
        {
            int pos = fast + "Content-Length: "u8.Length;
            int value = 0;
            while (pos < header.Length)
            {
                byte digit = (byte)(header[pos] - (byte)'0');
                if (digit <= 9)
                {
                    value = value * 10 + digit;
                    pos++;
                    continue;
                }

                return header[pos] == (byte)'\r' ? value : -1;
            }

            return value;
        }

        int lineStart = 0;
        while (lineStart < header.Length)
        {
            int lineEnd = IndexOfCrlf(header.Slice(lineStart));
            if (lineEnd < 0)
                lineEnd = header.Length - lineStart;

            ReadOnlySpan<byte> line = header.Slice(lineStart, lineEnd);
            if (StartsWithAsciiIgnoreCase(line, "content-length:"u8))
            {
                int pos = "content-length:"u8.Length;
                while (pos < line.Length && line[pos] == (byte)' ')
                    pos++;

                int value = 0;
                for (; pos < line.Length; pos++)
                {
                    byte digit = (byte)(line[pos] - (byte)'0');
                    if (digit > 9)
                        return -1;
                    value = value * 10 + digit;
                }

                return value;
            }

            lineStart += lineEnd + 2;
        }

        return 0;
    }

    /// <summary>
    /// Checks whether the HTTP request line starts with the expected method and exact path.
    /// Query strings are intentionally not accepted because Rinha endpoints do not use them.
    /// </summary>
    public static bool IsPath(ReadOnlySpan<byte> header, ReadOnlySpan<byte> method, ReadOnlySpan<byte> path)
    {
        if (!header.StartsWith(method))
            return false;

        ReadOnlySpan<byte> rest = header.Slice(method.Length);
        return rest.StartsWith(path) && rest.Length > path.Length && rest[path.Length] == (byte)' ';
    }

    /// <summary>
    /// Finds the first CRLF sequence inside a byte span, used for header-line iteration.
    /// </summary>
    private static int IndexOfCrlf(ReadOnlySpan<byte> value)
    {
        for (int i = 0; i < value.Length - 1; i++)
        {
            if (value[i] == (byte)'\r' && value[i + 1] == (byte)'\n')
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Performs an ASCII-only case-insensitive prefix check for fixed lowercase header names.
    /// The routine avoids culture rules and allocations because HTTP header names are ASCII.
    /// </summary>
    private static bool StartsWithAsciiIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix)
    {
        if (value.Length < prefix.Length)
            return false;

        for (int i = 0; i < prefix.Length; i++)
        {
            byte left = value[i];
            byte right = prefix[i];
            if (left >= (byte)'A' && left <= (byte)'Z')
                left = (byte)(left + 32);
            if (left != right)
                return false;
        }

        return true;
    }
}
