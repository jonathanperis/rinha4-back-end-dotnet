using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateSlimBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    var socketPath = Environment.GetEnvironmentVariable("SOCKET_PATH");
    if (!string.IsNullOrEmpty(socketPath))
    {
        options.ListenUnixSocket(socketPath);
    }
    else
    {
        options.ListenAnyIP(8080);
    }
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

var app = builder.Build();

// ── Load dataset ──
const short Scale = 10000;
const int Dims = 14;

string dataPath = Environment.GetEnvironmentVariable("DATA_PATH") ?? "/data/references.bin";

if (!File.Exists(dataPath))
{
    Console.WriteLine($"Dataset not found: {dataPath}");
    Environment.Exit(1);
}

Console.WriteLine("Loading dataset...");
var fileBytes = File.ReadAllBytes(dataPath);

ReadOnlySpan<byte> span = fileBytes;
int count = MemoryMarshal.Read<int>(span.Slice(0, 4));
int dims = MemoryMarshal.Read<int>(span.Slice(4, 4));
int scale = MemoryMarshal.Read<int>(span.Slice(8, 4));

Console.WriteLine($"Dataset: {count} vectors, {dims} dims, scale {scale}");

int headerSize = 12;

short[] vectors = MemoryMarshal.Cast<byte, short>(span.Slice(headerSize, count * Dims * 2)).ToArray();
byte[] labels = span.Slice(headerSize + count * Dims * 2, count).ToArray();

Console.WriteLine("Dataset loaded. Ready to serve.");

// ── Pre-computed responses ──
// 6 possible fraud scores: 0.0, 0.2, 0.4, 0.6, 0.8, 1.0
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
{
    mccRisk[prop.Name] = prop.Value.GetSingle();
}

// ── Endpoints ──
app.MapGet("/ready", () => Results.Ok());

app.MapPost("/fraud-score", (FraudRequest req) =>
{
    // ── Vectorize ──
    Span<float> fv = stackalloc float[Dims];

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
    Span<short> qv = stackalloc short[Dims];
    for (int i = 0; i < Dims; i++)
        qv[i] = (short)(fv[i] * Scale);

    // ── Brute-force top-5 ──
    // Fixed-size array, no heap alloc
    var top = new Top5();

    for (int vi = 0; vi < count; vi++)
    {
        int offset = vi * Dims;
        float dist = 0f;

        // Unrolled distance for 14 dims
        for (int i = 0; i < Dims; i++)
        {
            float d = qv[i] - vectors[offset + i];
            dist += d * d;
        }

        top.TryInsert(dist, labels[vi]);
    }

    int frauds = top.FraudCount();
    float fraudScore = frauds / 5.0f;
    int responseIndex = frauds; // 0..5 maps directly

    return Results.Bytes(responses[responseIndex].ToArray(), "application/json");
});

app.Run();

static float Clamp(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

// ── Top-5 tracker (fixed array, zero alloc) ──
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

        // Shift and insert
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
internal record FraudRequest(
    string Id,
    Transaction Transaction,
    Customer Customer,
    Merchant Merchant,
    Terminal Terminal,
    LastTransaction? LastTransaction
);

internal record Transaction(float Amount, int Installments, DateTime RequestedAt);
internal record Customer(float AvgAmount, int TxCount24h, List<string> KnownMerchants);
internal record Merchant(string Id, string Mcc, float AvgAmount);
internal record Terminal(bool IsOnline, bool CardPresent, float KmFromHome);
internal record LastTransaction(DateTime Timestamp, float KmFromCurrent);

[JsonSerializable(typeof(FraudRequest))]
internal partial class AppJsonContext : JsonSerializerContext { }
