using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// Simple offline validator: generates test payloads and verifies brute-force results

string dataDir = args.Length > 0 ? args[0] : "./data";
string binPath = Path.Combine(dataDir, "references.bin");
string normPath = Path.Combine(dataDir, "normalization.json");
string mccPath = Path.Combine(dataDir, "mcc_risk.json");

if (!File.Exists(binPath))
{
    Console.WriteLine($"Binary not found: {binPath}");
    Environment.Exit(1);
}

// Load binary
var fileBytes = File.ReadAllBytes(binPath);
ReadOnlySpan<byte> span = fileBytes;
int pos = 0;

int count = ReadInt32(span, ref pos);
int dims = ReadInt32(span, ref pos);
int paddedDims = ReadInt32(span, ref pos);
int scale = ReadInt32(span, ref pos);

int vectorsOffset = pos;
int labelsOffset = pos + count * paddedDims * 2;

ReadOnlySpan<short> allVectors = MemoryMarshal.Cast<byte, short>(span.Slice(vectorsOffset, count * paddedDims * 2));
ReadOnlySpan<byte> allLabels = span.Slice(labelsOffset, count);

Console.WriteLine($"Loaded: {count} vectors, {dims} dims (padded {paddedDims}), scale {scale}");

// Load normalization
var norms = JsonDocument.Parse(File.ReadAllText(normPath)).RootElement;
float maxAmount = norms.GetProperty("max_amount").GetSingle();
int maxInstallments = norms.GetProperty("max_installments").GetInt32();
float amountVsAvgRatio = norms.GetProperty("amount_vs_avg_ratio").GetSingle();
int maxMinutes = norms.GetProperty("max_minutes").GetInt32();
int maxKm = norms.GetProperty("max_km").GetInt32();
int maxTxCount24h = norms.GetProperty("max_tx_count_24h").GetInt32();
int maxMerchantAvgAmount = norms.GetProperty("max_merchant_avg_amount").GetInt32();

// Load MCC
var mccRisk = new float[10000];
var mccDoc = JsonDocument.Parse(File.ReadAllText(mccPath)).RootElement;
foreach (var prop in mccDoc.EnumerateObject())
{
    if (int.TryParse(prop.Name, out int code) && code >= 0 && code < 10000)
        mccRisk[code] = prop.Value.GetSingle();
}

// Generate test cases by sampling from dataset
var rand = new Random(42);
int numTests = args.Length > 1 ? int.Parse(args[1]) : 100;

Console.WriteLine($"\nRunning {numTests} validation tests...\n");

int correct = 0;
int falsePositives = 0;
int falseNegatives = 0;
double totalLatencyMs = 0;

for (int t = 0; t < numTests; t++)
{
    int idx = rand.Next(count);

    // Build a synthetic request from this vector
    var vector = allVectors.Slice(idx * paddedDims, dims);

    // De-vectorize to create a realistic payload
    float amount = (vector[0] / (float)scale) * maxAmount;
    int installments = (int)((vector[1] / (float)scale) * maxInstallments);
    float avgAmount = amount / ((vector[2] / (float)scale) * amountVsAvgRatio);
    int hour = (int)((vector[3] / (float)scale) * 23);
    int dow = (int)((vector[4] / (float)scale) * 6);

    // Run brute-force search
    var sw = System.Diagnostics.Stopwatch.StartNew();

    Span<short> qv = stackalloc short[paddedDims];
    for (int d = 0; d < dims; d++)
        qv[d] = vector[d];
    qv[dims] = 0;
    qv[dims + 1] = 0;

    var top = new Top5();
    for (int i = 0; i < count; i++)
    {
        float dist = 0;
        for (int d = 0; d < dims; d++)
        {
            float diff = qv[d] - allVectors[i * paddedDims + d];
            dist += diff * diff;
        }
        top.TryInsert(dist, allLabels[i]);
    }

    int frauds = top.FraudCount();
    float score = frauds / 5.0f;
    bool approved = score < 0.6f;

    sw.Stop();
    totalLatencyMs += sw.Elapsed.TotalMilliseconds;

    // Expected: the query vector itself should be in the top-5
    bool expectedApproved = allLabels[idx] == 0;

    if (approved == expectedApproved)
        correct++;
    else if (approved && !expectedApproved)
        falsePositives++;
    else
        falseNegatives++;
}

Console.WriteLine($"Results ({numTests} tests):");
Console.WriteLine($"  Correct:        {correct}/{numTests} ({100.0 * correct / numTests:F1}%)");
Console.WriteLine($"  False Positives: {falsePositives}");
Console.WriteLine($"  False Negatives: {falseNegatives}");
Console.WriteLine($"  Avg Latency:     {totalLatencyMs / numTests:F2} ms");
Console.WriteLine($"  Total Time:      {totalLatencyMs:F0} ms");

static int ReadInt32(ReadOnlySpan<byte> span, ref int pos)
{
    int val = MemoryMarshal.Read<int>(span.Slice(pos, 4));
    pos += 4;
    return val;
}

static float Clamp(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

ref struct Top5
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
