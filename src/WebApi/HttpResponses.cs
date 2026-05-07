/// <summary>
/// Owns prebuilt HTTP responses used by the raw server. This avoids per-request
/// JSON serialization, content-length formatting, and response-header allocation
/// on the hot fraud-score path.
/// </summary>
internal static class HttpResponses
{
    /// <summary>
    /// Complete HTTP 200 JSON responses indexed by fraud count from 0 through 5.
    /// Each body matches the competition response shape and keeps the connection alive.
    /// </summary>
    public static readonly ReadOnlyMemory<byte>[] FraudScores = BuildFraudScoreResponses();

    /// <summary>
    /// Empty keep-alive response for the readiness endpoint.
    /// </summary>
    public static readonly ReadOnlyMemory<byte> Ready = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n");

    /// <summary>
    /// Empty keep-alive response for malformed requests and invalid JSON payloads.
    /// </summary>
    public static readonly ReadOnlyMemory<byte> BadRequest = Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n");

    /// <summary>
    /// Empty keep-alive response for routes outside the Rinha contract.
    /// </summary>
    public static readonly ReadOnlyMemory<byte> NotFound = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n");

    /// <summary>
    /// Builds the six possible fraud-score responses once at startup. Scores can
    /// only be 0/5 through 5/5 because the classifier returns top-five fraud count.
    /// </summary>
    private static ReadOnlyMemory<byte>[] BuildFraudScoreResponses()
    {
        var responses = new ReadOnlyMemory<byte>[6];
        for (int i = 0; i <= 5; i++)
        {
            float score = i / 5.0f;
            bool approved = score < 0.6f;
            string json = $"{{\"approved\":{(approved ? "true" : "false")},\"fraud_score\":{score.ToString("F1", CultureInfo.InvariantCulture)}}}";
            byte[] body = Encoding.UTF8.GetBytes(json);
            responses[i] = BuildHttpResponse(body, "application/json");
        }

        return responses;
    }

    /// <summary>
    /// Builds a complete keep-alive HTTP 200 response for a fixed body and content type.
    /// The returned byte array is immutable by convention and can be reused safely.
    /// </summary>
    private static byte[] BuildHttpResponse(ReadOnlySpan<byte> body, string contentType)
    {
        byte[] header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: keep-alive\r\n\r\n");
        byte[] response = GC.AllocateUninitializedArray<byte>(header.Length + body.Length);
        header.CopyTo(response, 0);
        body.CopyTo(response.AsSpan(header.Length));
        return response;
    }
}
