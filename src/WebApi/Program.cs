using System.Runtime.InteropServices;
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
int scale = ReadInt32(span, ref pos);
int K = ReadInt32(span, ref pos);
int nprobe = ReadInt32(span, ref pos);

Console.WriteLine($"Dataset: {count:N0} vectors, {dims} dims, scale {scale}, K={K}, nprobe={nprobe}");

// Centroids: K * dims int16
short[] centroids = new short[K * dims];
for (int c = 0; c < K; c++)
    for (int d = 0; d < dims; d++)
        centroids[c * dims + d] = ReadInt16(span, ref pos);

// Posting list metadata
int[] plOffsets = new int[K];
int[] plLengths = new int[K];
for (int c = 0; c < K; c++)
{
    plOffsets[c] = ReadInt32(span, ref pos);
    plLengths[c] = ReadInt32(span, ref pos);
}

// Vectors: count * dims int16
short[] vectors = new short[count * dims];
for (int i = 0; i < count * dims; i++)
    vectors[i] = ReadInt16(span, ref pos);

// Labels: count bytes
byte[] labels = new byte[count];
for (int i = 0; i < count; i++)
    labels[i] = span[pos++];

Console.WriteLine("Dataset loaded. Ready to serve.");

// ── Pre-computed responses ──
ReadOnlyMemory<byte>[] responses = new ReadOnlyMemory<byte>[6];
for (int i = 0; i <= 5; i++)
{
    float score = i / 5.0f;
    bool approved = score < 0.6f;
    var json = $$"""{"approved":{{(approved ? "true" : "false")}},"fraud_score":{{score.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}}}""";
    responses[i] = System.Text.Encoding.UTF8.GetBytes(json);
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

// ── Load MCC risk ──
var mccPath = Path.Combine(Path.GetDirectoryName(dataPath)!, "mcc_risk.json");
var mccDoc = JsonDocument.Parse(File.ReadAllText(mccPath));
var mccRisk = new Dictionary<string, float>(StringComparer.Ordinal);
foreach (var prop in mccDoc.RootElement.EnumerateObject())
    mccRisk[prop.Name] = prop.Value.GetSingle();

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
    fv[12] = mccRisk.TryGetValue(req.Merchant.Mcc, out var risk) ? risk : 0.5f;
    fv[13] = Clamp(req.Merchant.AvgAmount / maxMerchantAvgAmount);

    // Quantize query to int16
    Span<short> qv = stackalloc short[dims];
    for (int i = 0; i < dims; i++)
        qv[i] = (short)(fv[i] * scale);

    // ── IVF Search ──
    // 1. Compute distance to all centroids
    Span<float> cdists = stackalloc float[K];
    for (int c = 0; c < K; c++)
    {
        float dist = 0;
        int coff = c * dims;
        for (int d = 0; d < dims; d++)
        {
            float diff = qv[d] - centroids[coff + d];
            dist += diff * diff;
        }
        cdists[c] = dist;
    }

    // 2. Find top-nprobe centroids (simple selection sort for small K)
    Span<int> bestC = stackalloc int[nprobe];
    for (int i = 0; i < nprobe; i++)
    {
        float best = float.MaxValue;
        int bestIdx = -1;
        for (int c = 0; c < K; c++)
        {
            if (cdists[c] < best)
            {
                best = cdists[c];
                bestIdx = c;
            }
        }
        bestC[i] = bestIdx;
        cdists[bestIdx] = float.MaxValue; // mark as used
    }

    // 3. Scan posting lists of selected centroids
    var top = new Top5();

    for (int i = 0; i < nprobe; i++)
    {
        int c = bestC[i];
        int offset = plOffsets[c];
        int length = plLengths[c];

        for (int vi = offset; vi < offset + length; vi++)
        {
            int voff = vi * dims;
            float dist = 0;

            for (int d = 0; d < dims; d++)
            {
                float diff = qv[d] - vectors[voff + d];
                dist += diff * diff;
            }

            top.TryInsert(dist, labels[vi]);
        }
    }

    int frauds = top.FraudCount();
    return Results.Bytes(responses[frauds].ToArray(), "application/json");
});

app.Run();

static float Clamp(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

static int ReadInt32(ReadOnlySpan<byte> span, ref int pos)
{
    int val = MemoryMarshal.Read<int>(span.Slice(pos, 4));
    pos += 4;
    return val;
}

static short ReadInt16(ReadOnlySpan<byte> span, ref int pos)
{
    short val = MemoryMarshal.Read<short>(span.Slice(pos, 2));
    pos += 2;
    return val;
}

// ── Top-5 tracker ──
struct Top5
{
    private float D0, D1, D2, D3, D4;
    private byte L0, L1, L2, L3, L4;

    public Top5()
    {
        D0 = D1 = D2 = D3 = D4 = float.MaxValue;
    }

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
