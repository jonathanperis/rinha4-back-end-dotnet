using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

string? socketPath = Environment.GetEnvironmentVariable("SOCKET_PATH");

// Default mode is the O(1) fine-bucket classifier. Exact modes are kept for
// validation and experiments, but they are too expensive for the target load.
bool exactSearch = string.Equals(Environment.GetEnvironmentVariable("SEARCH_MODE"), "exact", StringComparison.OrdinalIgnoreCase);
bool avxSearch = string.Equals(Environment.GetEnvironmentVariable("SEARCH_MODE"), "avx2", StringComparison.OrdinalIgnoreCase);

// ── Load dataset ──
string dataPath = Environment.GetEnvironmentVariable("DATA_PATH") ?? "/data/references.bin";

if (!File.Exists(dataPath))
{
    Console.WriteLine($"Dataset not found: {dataPath}");
    Environment.Exit(1);
}

Console.WriteLine("Loading dataset...");

var fileBytes = File.ReadAllBytes(dataPath);
ReadOnlySpan<byte> span = fileBytes;
int pos = 0;

const int BinaryMagic = 0x35444852;
const int GroupCount = FraudVectorizer.FineGroupCount;

int first = ReadInt32(span, ref pos);
bool hasGroupIndex = first == BinaryMagic;
int count = hasGroupIndex ? ReadInt32(span, ref pos) : first;
int dims = ReadInt32(span, ref pos);
int paddedDims = ReadInt32(span, ref pos);
int scale = ReadInt32(span, ref pos);
int groupOffsetsByteOffset = pos;
if (hasGroupIndex)
{
    // RHD5 embeds one offset per fine bucket plus a sentinel end offset.
    pos += (GroupCount + 1) * 4;
}

Console.WriteLine($"Dataset: {count:N0} vectors, {dims} dims (padded to {paddedDims}), scale {scale}, grouped {hasGroupIndex}");

// Vectors are packed first, labels second. Keeping labels separate makes exact
// scan cache behavior better than an interleaved vector+label record.
int vectorsByteOffset = pos;
int labelsByteOffset = pos + count * paddedDims * 2;
int totalFileSize = fileBytes.Length;

if (labelsByteOffset + count > totalFileSize)
{
    Console.WriteLine($"Invalid file size. Expected at least {labelsByteOffset + count}, got {totalFileSize}");
    Environment.Exit(1);
}

Console.WriteLine("Dataset loaded. Ready to serve.");

// ── Pre-computed responses ──
// Fraud score can only be 0/5, 1/5, ... 5/5. Prebuilding the full HTTP response
// avoids formatting, header building, and JSON serialization per request.
ReadOnlyMemory<byte>[] httpResponses = new ReadOnlyMemory<byte>[6];
for (int i = 0; i <= 5; i++)
{
    float score = i / 5.0f;
    bool approved = score < 0.6f;
    var json = $"{{\"approved\":{(approved ? "true" : "false")},\"fraud_score\":{score.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}}}";
    byte[] body = Encoding.UTF8.GetBytes(json);
    httpResponses[i] = BuildHttpResponse(body, "application/json");
}

ReadOnlyMemory<byte> readyResponse = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n");
ReadOnlyMemory<byte> badRequestResponse = Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n");
ReadOnlyMemory<byte> notFoundResponse = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n");

var groupResponseIndexes = hasGroupIndex && !exactSearch
    ? BuildGroupResponseIndexes(fileBytes, labelsByteOffset, groupOffsetsByteOffset, GroupCount)
    : Array.Empty<byte>();

// ── Load normalization constants ──
var normPath = Path.Combine(Path.GetDirectoryName(dataPath)!, "normalization.json");
var normDoc = JsonDocument.Parse(File.ReadAllText(normPath));
var norms = normDoc.RootElement;

float maxAmount = norms.GetProperty("max_amount").GetSingle();
int maxInstallments = norms.GetProperty("max_installments").GetInt32();
float amountVsAvgRatio = norms.GetProperty("amount_vs_avg_ratio").GetSingle();
int maxMinutes = norms.GetProperty("max_minutes").GetInt32();
int maxKm = norms.GetProperty("max_km").GetInt32();
int maxTxCount24h = norms.GetProperty("max_tx_count_24h").GetInt32();
int maxMerchantAvgAmount = norms.GetProperty("max_merchant_avg_amount").GetInt32();

// ── Load MCC risk into flat array ──
var mccPath = Path.Combine(Path.GetDirectoryName(dataPath)!, "mcc_risk.json");
var mccDoc = JsonDocument.Parse(File.ReadAllText(mccPath));
var mccRisk = new float[10000];
var mccRiskKnown = new bool[10000];
foreach (var prop in mccDoc.RootElement.EnumerateObject())
{
    if (int.TryParse(prop.Name, out int mccCode) && mccCode >= 0 && mccCode < 10000)
    {
        mccRisk[mccCode] = prop.Value.GetSingle();
        mccRiskKnown[mccCode] = true;
    }
}

ReadOnlyMemory<byte> ScoreFraudRequest(ReadOnlySpan<byte> body)
{
    // Parse only fields used by vectorization. Unknown request fields are
    // skipped so the parser stays tolerant without paying model-binding cost.
    FraudInput req;
    try
    {
        req = ParseFraudInput(body);
    }
    catch (JsonException)
    {
        return badRequestResponse;
    }

    // Keep per-request vectors on the stack. The raw HTTP path should not
    // allocate while scoring a normal request.
    Span<float> fv = stackalloc float[dims];

    fv[0] = Clamp(req.Amount / maxAmount);
    fv[1] = Clamp(req.Installments / (float)maxInstallments);
    fv[2] = Clamp((req.Amount / req.CustomerAvgAmount) / amountVsAvgRatio);

    int reqMinuteStamp = req.RequestedMinuteStamp;
    fv[3] = req.Hour / 23.0f;
    fv[4] = req.DayOfWeek / 6.0f;

    if (req.HasLastTransaction)
    {
        int minutes = reqMinuteStamp - req.LastMinuteStamp;
        fv[5] = Clamp(minutes / (float)maxMinutes);
        fv[6] = Clamp(req.KmFromCurrent / maxKm);
    }
    else
    {
        fv[5] = -1.0f;
        fv[6] = -1.0f;
    }

    fv[7] = Clamp(req.KmFromHome / maxKm);
    fv[8] = Clamp(req.TxCount24h / (float)maxTxCount24h);
    fv[9] = req.IsOnline ? 1.0f : 0.0f;
    fv[10] = req.CardPresent ? 1.0f : 0.0f;
    fv[11] = req.UnknownMerchant ? 1.0f : 0.0f;

    fv[12] = req.MccCode >= 0 && req.MccCode < mccRisk.Length && mccRiskKnown[req.MccCode] ? mccRisk[req.MccCode] : 0.5f;

    fv[13] = Clamp(req.MerchantAvgAmount / maxMerchantAvgAmount);

    // Quantize query to the same int16 scale used by references.bin.
    Span<short> qv = stackalloc short[paddedDims];
    for (int i = 0; i < dims; i++)
        qv[i] = (short)(fv[i] * scale);
    qv[dims] = 0;
    qv[dims + 1] = 0;

    if (hasGroupIndex && !exactSearch)
    {
        // Main competition path: map vector to fine bucket, then return that
        // bucket's precomputed majority response.
        int queryGroup = FraudVectorizer.FineVectorGroup(qv[5], qv[6], qv[0], qv[7], qv[9], qv[10], qv[11], scale);
        return httpResponses[groupResponseIndexes[queryGroup]];
    }

    // ── Brute-force AVX2 Search ──
    var top = new Top5();

    unsafe
    {
        fixed (byte* dataPtr = fileBytes)
        fixed (short* qPtr = qv)
        {
            short* vecPtr = (short*)(dataPtr + vectorsByteOffset);
            byte* labelPtr = dataPtr + labelsByteOffset;

            if (Avx2.IsSupported && avxSearch)
            {
                SearchAvx2(vecPtr, labelPtr, count, qPtr, ref top);
            }
            else
            {
                SearchScalarPruned(vecPtr, labelPtr, 0, count, qPtr, ref top);
            }
        }
    }

    int frauds = top.FraudCount();
    return httpResponses[frauds];
}

async Task RunRawHttpServerAsync()
{
    // Bind directly to the same endpoint Kestrel used: UDS in Docker, TCP for
    // local single-process runs.
    using var listener = string.IsNullOrEmpty(socketPath)
        ? new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        : new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

    if (string.IsNullOrEmpty(socketPath))
    {
        listener.Bind(new IPEndPoint(IPAddress.Any, 8080));
    }
    else
    {
        if (File.Exists(socketPath))
            File.Delete(socketPath);

        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
    }

    listener.Listen(8192);

    if (!string.IsNullOrEmpty(socketPath) && OperatingSystem.IsLinux())
        AllowSocketClients(socketPath);

    Console.WriteLine(string.IsNullOrEmpty(socketPath)
        ? "Raw HTTP/1 server listening on 0.0.0.0:8080"
        : $"Raw HTTP/1 server listening on {socketPath}");

    while (true)
    {
        Socket client = await listener.AcceptAsync();
        _ = HandleConnectionAsync(client);
    }
}

async Task HandleConnectionAsync(Socket socket)
{
    using (socket)
    {
        // One pooled buffer per client connection. k6/nginx do not pipeline in
        // a way that needs a larger request buffer for this payload shape.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        int start = 0;
        int end = 0;

        try
        {
            while (true)
            {
                int headerEnd;
                while ((headerEnd = FindHeaderEnd(buffer, start, end)) < 0)
                {
                    // Wait until a full HTTP header is available. Oversized
                    // headers are rejected instead of resizing in the hot path.
                    if (end == buffer.Length)
                    {
                        await SendAllAsync(socket, badRequestResponse);
                        return;
                    }

                    int read = await socket.ReceiveAsync(buffer.AsMemory(end), SocketFlags.None);
                    if (read == 0)
                        return;

                    end += read;
                }

                int contentLength = GetContentLength(buffer.AsSpan(start, headerEnd - start));
                if (contentLength < 0 || contentLength > 8192)
                {
                    await SendAllAsync(socket, badRequestResponse);
                    return;
                }

                int bodyStart = headerEnd + 4;
                int requestEnd = bodyStart + contentLength;
                while (end < requestEnd)
                {
                    // Body can arrive in a later TCP/UDS read. Keep filling the
                    // same buffer until Content-Length bytes are present.
                    if (end == buffer.Length)
                    {
                        await SendAllAsync(socket, badRequestResponse);
                        return;
                    }

                    int read = await socket.ReceiveAsync(buffer.AsMemory(end), SocketFlags.None);
                    if (read == 0)
                        return;

                    end += read;
                }

                ReadOnlyMemory<byte> response = SelectResponse(buffer.AsSpan(start, headerEnd - start), buffer.AsSpan(bodyStart, contentLength));
                await SendAllAsync(socket, response);

                int remaining = end - requestEnd;
                if (remaining > 0)
                {
                    // Preserve bytes from a pipelined next request. Most clients
                    // will not pipeline, but this keeps the loop HTTP/1-correct.
                    Buffer.BlockCopy(buffer, requestEnd, buffer, 0, remaining);
                }

                start = 0;
                end = remaining;
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

ReadOnlyMemory<byte> SelectResponse(ReadOnlySpan<byte> header, ReadOnlySpan<byte> body)
{
    // Route by the request line only. Rinha endpoints do not use query strings.
    if (IsPath(header, "GET "u8, "/ready"u8))
        return readyResponse;

    if (IsPath(header, "POST "u8, "/fraud-score"u8))
        return body.IsEmpty ? badRequestResponse : ScoreFraudRequest(body);

    return notFoundResponse;
}

await RunRawHttpServerAsync();

[SupportedOSPlatform("linux")]
static void AllowSocketClients(string socketPath)
{
    const UnixFileMode Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                              UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                              UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

#pragma warning disable CA1416
    File.SetUnixFileMode(socketPath, Mode);
#pragma warning restore CA1416
}

// ── AVX2 Brute-force Search ──
static unsafe void SearchAvx2(short* vecPtr, byte* labelPtr, int count, short* qPtr, ref Top5 top)
{
    const int PaddedDims = 16;
    var qVec = Avx.LoadVector256(qPtr);

    for (int i = 0; i < count; i++)
    {
        short* vPtr = vecPtr + i * PaddedDims;
        var vVec = Avx.LoadVector256(vPtr);

        var diff = Avx2.Subtract(qVec, vVec);

        // Widen to int32
        var (diffLo, diffHi) = Vector256.Widen(diff);

        // Square
        var sqLo = Avx2.MultiplyLow(diffLo, diffLo);
        var sqHi = Avx2.MultiplyLow(diffHi, diffHi);

        // Sum
        var sum = Avx2.Add(sqLo, sqHi);

        // Horizontal sum
        int dist = HorizontalSum256(sum);

        if (dist < top.WorstBound)
            top.TryInsert(dist, labelPtr[i]);
    }
}

// ── Scalar Fallback ──
static unsafe void SearchScalarPruned(short* vecPtr, byte* labelPtr, int start, int end, short* qPtr, ref Top5 top)
{
    const int PaddedDims = 16;
    for (int i = start; i < end; i++)
    {
        short* vPtr = vecPtr + i * PaddedDims;
        int worst = top.WorstBound;
        int dist = 0;
        int diff;

        diff = qPtr[5] - vPtr[5]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
        diff = qPtr[6] - vPtr[6]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
        diff = qPtr[2] - vPtr[2]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
        diff = qPtr[7] - vPtr[7]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
        diff = qPtr[0] - vPtr[0]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
        diff = qPtr[8] - vPtr[8]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
        diff = qPtr[3] - vPtr[3]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
        diff = qPtr[4] - vPtr[4]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
        diff = qPtr[12] - vPtr[12]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
        diff = qPtr[13] - vPtr[13]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
        diff = qPtr[1] - vPtr[1]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
        diff = qPtr[11] - vPtr[11]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
        diff = qPtr[9] - vPtr[9]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
        diff = qPtr[10] - vPtr[10]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;

        if (dist < top.WorstBound)
            top.TryInsert(dist, labelPtr[i]);
    }
}

[MethodImpl(MethodImplOptions.AggressiveInlining)]
static bool AddSquaredWithinBound(ref int dist, int diff, int bound)
{
    int square = diff * diff;
    if (dist >= bound - square)
        return false;

    dist += square;
    return true;
}

// ── AVX2 Horizontal Sum ──
[MethodImpl(MethodImplOptions.AggressiveInlining)]
static int HorizontalSum256(Vector256<int> v)
{
    // Extract upper and lower 128-bit halves
    var lower = v.GetLower();
    var upper = v.GetUpper();

    // Add them
    var sum128 = Sse2.Add(lower, upper);

    // Shuffle and add: [a,b,c,d] → [a+c, b+d, a+c, b+d]
    var shuffled = Sse2.Shuffle(sum128, 0b_11_10_11_10);
    var sum64 = Sse2.Add(sum128, shuffled);

    // Shuffle and add: [x,y,x,y] → [x+y, x+y, x+y, x+y]
    var shuffled2 = Sse2.Shuffle(sum64, 0b_01_00_01_00);
    var sum32 = Sse2.Add(sum64, shuffled2);

    return Sse2.ConvertToInt32(sum32);
}

static float Clamp(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

static byte[] BuildHttpResponse(ReadOnlySpan<byte> body, string contentType)
{
    byte[] header = Encoding.ASCII.GetBytes(
        $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: keep-alive\r\n\r\n");
    byte[] response = GC.AllocateUninitializedArray<byte>(header.Length + body.Length);
    header.CopyTo(response, 0);
    body.CopyTo(response.AsSpan(header.Length));
    return response;
}

static async ValueTask SendAllAsync(Socket socket, ReadOnlyMemory<byte> data)
{
    while (!data.IsEmpty)
    {
        int sent = await socket.SendAsync(data, SocketFlags.None);
        if (sent <= 0)
            return;

        data = data.Slice(sent);
    }
}

static int FindHeaderEnd(byte[] buffer, int start, int end)
{
    for (int i = start; i <= end - 4; i++)
    {
        if (buffer[i] == (byte)'\r' &&
            buffer[i + 1] == (byte)'\n' &&
            buffer[i + 2] == (byte)'\r' &&
            buffer[i + 3] == (byte)'\n')
        {
            return i;
        }
    }

    return -1;
}

static int GetContentLength(ReadOnlySpan<byte> header)
{
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

static bool IsPath(ReadOnlySpan<byte> header, ReadOnlySpan<byte> method, ReadOnlySpan<byte> path)
{
    if (!header.StartsWith(method))
        return false;

    ReadOnlySpan<byte> rest = header.Slice(method.Length);
    return rest.StartsWith(path) && rest.Length > path.Length && rest[path.Length] == (byte)' ';
}

static int IndexOfCrlf(ReadOnlySpan<byte> value)
{
    for (int i = 0; i < value.Length - 1; i++)
    {
        if (value[i] == (byte)'\r' && value[i + 1] == (byte)'\n')
            return i;
    }

    return -1;
}

static bool StartsWithAsciiIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix)
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

static FraudInput ParseFraudInput(ReadOnlySpan<byte> body)
{
    var reader = new Utf8JsonReader(body);
    var input = new FraudInput();
    string? merchantId = null;
    string? known0 = null;
    string? known1 = null;
    string? known2 = null;
    string? known3 = null;
    List<string>? knownExtra = null;

    RequireRead(ref reader);
    if (reader.TokenType != JsonTokenType.StartObject)
        throw new JsonException();

    while (RequireRead(ref reader) && reader.TokenType != JsonTokenType.EndObject)
    {
        if (reader.TokenType != JsonTokenType.PropertyName)
            throw new JsonException();

        if (reader.ValueTextEquals("transaction"u8))
        {
            RequireRead(ref reader);
            ReadTransaction(ref reader, ref input);
        }
        else if (reader.ValueTextEquals("customer"u8))
        {
            RequireRead(ref reader);
            ReadCustomer(ref reader, ref input, ref known0, ref known1, ref known2, ref known3, ref knownExtra);
        }
        else if (reader.ValueTextEquals("merchant"u8))
        {
            RequireRead(ref reader);
            ReadMerchant(ref reader, ref input, ref merchantId);
        }
        else if (reader.ValueTextEquals("terminal"u8))
        {
            RequireRead(ref reader);
            ReadTerminal(ref reader, ref input);
        }
        else if (reader.ValueTextEquals("last_transaction"u8))
        {
            RequireRead(ref reader);
            ReadLastTransaction(ref reader, ref input);
        }
        else
        {
            RequireRead(ref reader);
            reader.Skip();
        }
    }

    input.UnknownMerchant = !MerchantIsKnown(merchantId, known0, known1, known2, known3, knownExtra);
    return input;
}

static void ReadTransaction(ref Utf8JsonReader reader, ref FraudInput input)
{
    if (reader.TokenType != JsonTokenType.StartObject)
        throw new JsonException();

    while (RequireRead(ref reader) && reader.TokenType != JsonTokenType.EndObject)
    {
        if (reader.TokenType != JsonTokenType.PropertyName)
            throw new JsonException();

        if (reader.ValueTextEquals("amount"u8))
        {
            RequireRead(ref reader);
            input.Amount = reader.GetSingle();
        }
        else if (reader.ValueTextEquals("installments"u8))
        {
            RequireRead(ref reader);
            input.Installments = reader.GetInt32();
        }
        else if (reader.ValueTextEquals("requested_at"u8))
        {
            RequireRead(ref reader);
            ReadIsoUtc(ref reader, out input.Hour, out input.DayOfWeek, out input.RequestedMinuteStamp);
        }
        else
        {
            RequireRead(ref reader);
            reader.Skip();
        }
    }
}

static void ReadCustomer(
    ref Utf8JsonReader reader,
    ref FraudInput input,
    ref string? known0,
    ref string? known1,
    ref string? known2,
    ref string? known3,
    ref List<string>? knownExtra)
{
    if (reader.TokenType != JsonTokenType.StartObject)
        throw new JsonException();

    while (RequireRead(ref reader) && reader.TokenType != JsonTokenType.EndObject)
    {
        if (reader.TokenType != JsonTokenType.PropertyName)
            throw new JsonException();

        if (reader.ValueTextEquals("avg_amount"u8))
        {
            RequireRead(ref reader);
            input.CustomerAvgAmount = reader.GetSingle();
        }
        else if (reader.ValueTextEquals("tx_count_24h"u8))
        {
            RequireRead(ref reader);
            input.TxCount24h = reader.GetInt32();
        }
        else if (reader.ValueTextEquals("known_merchants"u8))
        {
            RequireRead(ref reader);
            ReadKnownMerchants(ref reader, ref known0, ref known1, ref known2, ref known3, ref knownExtra);
        }
        else
        {
            RequireRead(ref reader);
            reader.Skip();
        }
    }
}

static void ReadMerchant(ref Utf8JsonReader reader, ref FraudInput input, ref string? merchantId)
{
    if (reader.TokenType != JsonTokenType.StartObject)
        throw new JsonException();

    while (RequireRead(ref reader) && reader.TokenType != JsonTokenType.EndObject)
    {
        if (reader.TokenType != JsonTokenType.PropertyName)
            throw new JsonException();

        if (reader.ValueTextEquals("id"u8))
        {
            RequireRead(ref reader);
            merchantId = reader.GetString();
        }
        else if (reader.ValueTextEquals("mcc"u8))
        {
            RequireRead(ref reader);
            input.MccCode = ReadMccCode(ref reader);
        }
        else if (reader.ValueTextEquals("avg_amount"u8))
        {
            RequireRead(ref reader);
            input.MerchantAvgAmount = reader.GetSingle();
        }
        else
        {
            RequireRead(ref reader);
            reader.Skip();
        }
    }
}

static void ReadTerminal(ref Utf8JsonReader reader, ref FraudInput input)
{
    if (reader.TokenType != JsonTokenType.StartObject)
        throw new JsonException();

    while (RequireRead(ref reader) && reader.TokenType != JsonTokenType.EndObject)
    {
        if (reader.TokenType != JsonTokenType.PropertyName)
            throw new JsonException();

        if (reader.ValueTextEquals("is_online"u8))
        {
            RequireRead(ref reader);
            input.IsOnline = reader.GetBoolean();
        }
        else if (reader.ValueTextEquals("card_present"u8))
        {
            RequireRead(ref reader);
            input.CardPresent = reader.GetBoolean();
        }
        else if (reader.ValueTextEquals("km_from_home"u8))
        {
            RequireRead(ref reader);
            input.KmFromHome = reader.GetSingle();
        }
        else
        {
            RequireRead(ref reader);
            reader.Skip();
        }
    }
}

static void ReadLastTransaction(ref Utf8JsonReader reader, ref FraudInput input)
{
    if (reader.TokenType == JsonTokenType.Null)
        return;

    if (reader.TokenType != JsonTokenType.StartObject)
        throw new JsonException();

    input.HasLastTransaction = true;

    while (RequireRead(ref reader) && reader.TokenType != JsonTokenType.EndObject)
    {
        if (reader.TokenType != JsonTokenType.PropertyName)
            throw new JsonException();

        if (reader.ValueTextEquals("timestamp"u8))
        {
            RequireRead(ref reader);
            ReadIsoUtc(ref reader, out _, out _, out input.LastMinuteStamp);
        }
        else if (reader.ValueTextEquals("km_from_current"u8))
        {
            RequireRead(ref reader);
            input.KmFromCurrent = reader.GetSingle();
        }
        else
        {
            RequireRead(ref reader);
            reader.Skip();
        }
    }
}

static void ReadKnownMerchants(
    ref Utf8JsonReader reader,
    ref string? known0,
    ref string? known1,
    ref string? known2,
    ref string? known3,
    ref List<string>? knownExtra)
{
    if (reader.TokenType != JsonTokenType.StartArray)
        throw new JsonException();

    int count = 0;
    while (RequireRead(ref reader) && reader.TokenType != JsonTokenType.EndArray)
    {
        string merchant = reader.GetString() ?? string.Empty;
        switch (count++)
        {
            case 0: known0 = merchant; break;
            case 1: known1 = merchant; break;
            case 2: known2 = merchant; break;
            case 3: known3 = merchant; break;
            default:
                knownExtra ??= new List<string>(4);
                knownExtra.Add(merchant);
                break;
        }
    }
}

static bool MerchantIsKnown(string? merchantId, string? known0, string? known1, string? known2, string? known3, List<string>? knownExtra)
{
    if (merchantId is null)
        return false;

    if (merchantId == known0 || merchantId == known1 || merchantId == known2 || merchantId == known3)
        return true;

    if (knownExtra is null)
        return false;

    for (int i = 0; i < knownExtra.Count; i++)
    {
        if (merchantId == knownExtra[i])
            return true;
    }

    return false;
}

static void ReadIsoUtc(ref Utf8JsonReader reader, out int hour, out int dayOfWeek, out int minuteStamp)
{
    if (reader.TokenType != JsonTokenType.String)
        throw new JsonException();

    if (reader.HasValueSequence)
    {
        FraudVectorizer.ParseIsoUtc(reader.GetString()!, out hour, out dayOfWeek, out minuteStamp);
        return;
    }

    FraudVectorizer.ParseIsoUtc(reader.ValueSpan, out hour, out dayOfWeek, out minuteStamp);
}

static int ReadMccCode(ref Utf8JsonReader reader)
{
    if (reader.TokenType == JsonTokenType.Number)
        return reader.GetInt32();

    if (reader.TokenType != JsonTokenType.String)
        throw new JsonException();

    ReadOnlySpan<byte> span = reader.HasValueSequence ? Encoding.UTF8.GetBytes(reader.GetString()!) : reader.ValueSpan;
    int code = 0;
    for (int i = 0; i < span.Length; i++)
    {
        byte digit = (byte)(span[i] - (byte)'0');
        if (digit > 9)
            return -1;
        code = code * 10 + digit;
    }

    return code;
}

static bool RequireRead(ref Utf8JsonReader reader)
{
    if (!reader.Read())
        throw new JsonException();
    return true;
}

static int ReadInt32(ReadOnlySpan<byte> span, ref int pos)
{
    int val = MemoryMarshal.Read<int>(span.Slice(pos, 4));
    pos += 4;
    return val;
}

static int ReadInt32At(byte[] bytes, int pos) => MemoryMarshal.Read<int>(bytes.AsSpan(pos, 4));

static int ReadGroupOffset(byte[] bytes, int groupOffsetsByteOffset, int group) =>
    ReadInt32At(bytes, groupOffsetsByteOffset + group * 4);

static byte[] BuildGroupResponseIndexes(byte[] fileBytes, int labelsByteOffset, int groupOffsetsByteOffset, int groupCount)
{
    var indexes = GC.AllocateUninitializedArray<byte>(groupCount);

    for (int group = 0; group < groupCount; group++)
    {
        int start = ReadGroupOffset(fileBytes, groupOffsetsByteOffset, group);
        int end = ReadGroupOffset(fileBytes, groupOffsetsByteOffset, group + 1);
        int total = end - start;
        if (total == 0)
        {
            indexes[group] = 0;
            continue;
        }

        int frauds = 0;
        for (int i = start; i < end; i++)
            frauds += fileBytes[labelsByteOffset + i];

        indexes[group] = (byte)((frauds * 5 + total / 2) / total);
    }

    return indexes;
}

// ── Top-5 tracker ──
ref struct Top5
{
    private int D0, D1, D2, D3, D4;
    private byte L0, L1, L2, L3, L4;

    public Top5()
    {
        D0 = D1 = D2 = D3 = D4 = int.MaxValue;
    }

    public int WorstBound => D4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TryInsert(int dist, byte label)
    {
        if (dist >= D4) return;

        if (dist < D0)
        {
            D4 = D3; L4 = L3;
            D3 = D2; L3 = L2;
            D2 = D1; L2 = L1;
            D1 = D0; L1 = L0;
            D0 = dist; L0 = label;
        }
        else if (dist < D1)
        {
            D4 = D3; L4 = L3;
            D3 = D2; L3 = L2;
            D2 = D1; L2 = L1;
            D1 = dist; L1 = label;
        }
        else if (dist < D2)
        {
            D4 = D3; L4 = L3;
            D3 = D2; L3 = L2;
            D2 = dist; L2 = label;
        }
        else if (dist < D3)
        {
            D4 = D3; L4 = L3;
            D3 = dist; L3 = label;
        }
        else
        {
            D4 = dist; L4 = label;
        }
    }

    public int FraudCount()
    {
        int c = 0;
        if (L0 == 1) c++;
        if (L1 == 1) c++;
        if (L2 == 1) c++;
        if (L3 == 1) c++;
        if (L4 == 1) c++;
        return c;
    }
}

struct FraudInput
{
    public float Amount;
    public int Installments;
    public int Hour;
    public int DayOfWeek;
    public int RequestedMinuteStamp;
    public float CustomerAvgAmount;
    public int TxCount24h;
    public int MccCode;
    public float MerchantAvgAmount;
    public bool IsOnline;
    public bool CardPresent;
    public float KmFromHome;
    public bool HasLastTransaction;
    public int LastMinuteStamp;
    public float KmFromCurrent;
    public bool UnknownMerchant;
}
