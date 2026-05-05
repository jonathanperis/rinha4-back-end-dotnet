using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateSlimBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    var socketPath = Environment.GetEnvironmentVariable("SOCKET_PATH");
    if (!string.IsNullOrEmpty(socketPath))
        options.ListenUnixSocket(socketPath);
    else
        options.ListenAnyIP(8080);
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

var app = builder.Build();

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

int count = ReadInt32(span, ref pos);
int dims = ReadInt32(span, ref pos);
int paddedDims = ReadInt32(span, ref pos);
int scale = ReadInt32(span, ref pos);

Console.WriteLine($"Dataset: {count:N0} vectors, {dims} dims (padded to {paddedDims}), scale {scale}");

// Calculate offsets
int vectorsByteOffset = pos;
int labelsByteOffset = pos + count * paddedDims * 2;
int totalFileSize = fileBytes.Length;

if (labelsByteOffset + count > totalFileSize)
{
    Console.WriteLine($"Invalid file size. Expected at least {labelsByteOffset + count}, got {totalFileSize}");
    Environment.Exit(1);
}

// Pin arrays for AVX2
var vectorsArray = GC.AllocateUninitializedArray<short>(count * paddedDims, pinned: true);
var labelsArray = GC.AllocateUninitializedArray<byte>(count, pinned: true);

// Copy data into pinned arrays
Buffer.BlockCopy(fileBytes, vectorsByteOffset, vectorsArray, 0, count * paddedDims * 2);
Buffer.BlockCopy(fileBytes, labelsByteOffset, labelsArray, 0, count);

// Release fileBytes - vectorsArray and labelsArray now own the data
fileBytes = null!;

Console.WriteLine("Dataset loaded. Ready to serve.");

// ── Pre-computed responses ──
ReadOnlyMemory<byte>[] responses = new ReadOnlyMemory<byte>[6];
for (int i = 0; i <= 5; i++)
{
    float score = i / 5.0f;
    bool approved = score < 0.6f;
    var json = $"{{\"approved\":{(approved ? "true" : "false")},\"fraud_score\":{score.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}}}";
    responses[i] = Encoding.UTF8.GetBytes(json);
}

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
foreach (var prop in mccDoc.RootElement.EnumerateObject())
{
    if (int.TryParse(prop.Name, out int mccCode) && mccCode >= 0 && mccCode < 10000)
        mccRisk[mccCode] = prop.Value.GetSingle();
}

// ── Endpoints ──
app.MapGet("/ready", () => Results.Ok());

app.MapPost("/fraud-score", (FraudRequest req) =>
{
    // ── Vectorize ──
    Span<float> fv = stackalloc float[dims];

    fv[0] = Clamp(req.Transaction.Amount / maxAmount);
    fv[1] = Clamp(req.Transaction.Installments / (float)maxInstallments);
    fv[2] = Clamp((req.Transaction.Amount / req.Customer.AvgAmount) / amountVsAvgRatio);

    var reqAt = req.Transaction.RequestedAt;
    fv[3] = reqAt.Hour / 23.0f;
    fv[4] = ((int)reqAt.DayOfWeek) / 6.0f;

    if (req.LastTransaction != null)
    {
        double minutes = (reqAt - req.LastTransaction.Timestamp).TotalMinutes;
        fv[5] = Clamp((float)(minutes / maxMinutes));
        fv[6] = Clamp(req.LastTransaction.KmFromCurrent / maxKm);
    }
    else
    {
        fv[5] = -1.0f;
        fv[6] = -1.0f;
    }

    fv[7] = Clamp(req.Terminal.KmFromHome / maxKm);
    fv[8] = Clamp(req.Customer.TxCount24h / (float)maxTxCount24h);
    fv[9] = req.Terminal.IsOnline ? 1.0f : 0.0f;
    fv[10] = req.Terminal.CardPresent ? 1.0f : 0.0f;
    fv[11] = req.Customer.KnownMerchants.Contains(req.Merchant.Id) ? 0.0f : 1.0f;

    // Parse MCC and look up risk
    float mccValue = 0.5f;
    if (int.TryParse(req.Merchant.Mcc, out int mccCode2) && mccCode2 >= 0 && mccCode2 < 10000)
        mccValue = mccRisk[mccCode2];
    fv[12] = mccValue;

    fv[13] = Clamp(req.Merchant.AvgAmount / maxMerchantAvgAmount);

    // Quantize query to int16
    Span<short> qv = stackalloc short[paddedDims];
    for (int i = 0; i < dims; i++)
        qv[i] = (short)(fv[i] * scale);
    qv[dims] = 0;
    qv[dims + 1] = 0;

    // ── Brute-force AVX2 Search ──
    var top = new Top5();

    unsafe
    {
        fixed (short* vecPtr = vectorsArray)
        fixed (byte* labelPtr = labelsArray)
        fixed (short* qPtr = qv)
        {
            if (Avx2.IsSupported)
            {
                SearchAvx2(vecPtr, labelPtr, count, qPtr, ref top);
            }
            else
            {
                SearchScalar(vecPtr, labelPtr, count, qPtr, ref top);
            }
        }
    }

    int frauds = top.FraudCount();
    return Results.Bytes(responses[frauds], "application/json");
});

app.Run();

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
        float dist = HorizontalSum256(sum);

        if (dist < top.WorstBound)
            top.TryInsert(dist, labelPtr[i]);
    }
}

// ── Scalar Fallback ──
static unsafe void SearchScalar(short* vecPtr, byte* labelPtr, int count, short* qPtr, ref Top5 top)
{
    const int PaddedDims = 16;
    for (int i = 0; i < count; i++)
    {
        short* vPtr = vecPtr + i * PaddedDims;
        float dist = 0;

        for (int d = 0; d < 14; d++)
        {
            float diff = qPtr[d] - vPtr[d];
            dist += diff * diff;
        }

        if (dist < top.WorstBound)
            top.TryInsert(dist, labelPtr[i]);
    }
}

// ── AVX2 Horizontal Sum ──
[MethodImpl(MethodImplOptions.AggressiveInlining)]
static float HorizontalSum256(Vector256<int> v)
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

static int ReadInt32(ReadOnlySpan<byte> span, ref int pos)
{
    int val = MemoryMarshal.Read<int>(span.Slice(pos, 4));
    pos += 4;
    return val;
}

// ── Top-5 tracker ──
ref struct Top5
{
    private float D0, D1, D2, D3, D4;
    private byte L0, L1, L2, L3, L4;

    public Top5()
    {
        D0 = D1 = D2 = D3 = D4 = float.MaxValue;
    }

    public float WorstBound => D4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TryInsert(float dist, byte label)
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

// ── JSON contracts ──
internal sealed record FraudRequest(
    string Id,
    Transaction Transaction,
    Customer Customer,
    Merchant Merchant,
    Terminal Terminal,
    LastTransaction? LastTransaction
);

internal sealed record Transaction(float Amount, int Installments, DateTime RequestedAt);
internal sealed record Customer(float AvgAmount, int TxCount24h, List<string> KnownMerchants);
internal sealed record Merchant(string Id, string Mcc, float AvgAmount);
internal sealed record Terminal(bool IsOnline, bool CardPresent, float KmFromHome);
internal sealed record LastTransaction(DateTime Timestamp, float KmFromCurrent);

[JsonSerializable(typeof(FraudRequest))]
internal partial class AppJsonContext : JsonSerializerContext { }
