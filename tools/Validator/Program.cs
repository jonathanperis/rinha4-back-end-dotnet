using System.Runtime.InteropServices;
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

const int BinaryMagic = 0x35444852;
const int GroupCount = FraudVectorizer.FineGroupCount;

int first = ReadInt32(span, ref pos);
bool hasGroupIndex = first == BinaryMagic;
int count = hasGroupIndex ? ReadInt32(span, ref pos) : first;
int dims = ReadInt32(span, ref pos);
int paddedDims = ReadInt32(span, ref pos);
int scale = ReadInt32(span, ref pos);
var groupOffsets = new int[GroupCount + 1];
if (hasGroupIndex)
{
    for (int i = 0; i <= GroupCount; i++)
        groupOffsets[i] = ReadInt32(span, ref pos);
}

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
int approxMatches = 0;
int approxFalsePositives = 0;
int approxFalseNegatives = 0;
int majorityMatches = 0;
int majorityFalsePositives = 0;
int majorityFalseNegatives = 0;
double totalLatencyMs = 0;
double approxTotalLatencyMs = 0;
var groupFraudCounts = new int[hasGroupIndex ? GroupCount : 1];
if (hasGroupIndex)
{
    // Precompute fraud counts per bucket so bucket-majority decisions can be
    // compared against exact KNN decisions without rescanning every test.
    for (int group = 0; group < GroupCount; group++)
    {
        int fraudsInGroup = 0;
        for (int i = groupOffsets[group]; i < groupOffsets[group + 1]; i++)
            fraudsInGroup += allLabels[i];
        groupFraudCounts[group] = fraudsInGroup;
    }
}

for (int t = 0; t < numTests; t++)
{
    int idx = rand.Next(count);

    // Build a synthetic request from this vector
    var vector = allVectors.Slice(idx * paddedDims, dims);

    // Run brute-force search
    var sw = System.Diagnostics.Stopwatch.StartNew();

    ReadOnlySpan<short> qv = vector;

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

    sw.Restart();
    var approxTop = new Top5();
    // Same-bucket approximation is not the production path anymore, but it is
    // useful evidence when choosing bucket dimensions.
    int queryGroup = hasGroupIndex
        ? FraudVectorizer.FineVectorGroup(qv[5], qv[6], qv[0], qv[7], qv[9], qv[10], qv[11], scale)
        : 0;
    int approxStart = hasGroupIndex ? groupOffsets[queryGroup] : 0;
    int approxEnd = hasGroupIndex ? groupOffsets[queryGroup + 1] : count;
    for (int i = approxStart; i < approxEnd; i++)
    {
        float dist = 0;
        for (int d = 0; d < dims; d++)
        {
            float diff = qv[d] - allVectors[i * paddedDims + d];
            dist += diff * diff;
        }
        approxTop.TryInsert(dist, allLabels[i]);
    }
    int approxFrauds = approxTop.FraudCount();
    bool approxApproved = approxFrauds < 3;
    sw.Stop();
    approxTotalLatencyMs += sw.Elapsed.TotalMilliseconds;

    // Expected: the query vector itself should be in the top-5
    bool expectedApproved = allLabels[idx] == 0;

    if (approved == expectedApproved)
        correct++;
    else if (approved && !expectedApproved)
        falsePositives++;
    else
        falseNegatives++;

    if (approxApproved == approved)
        approxMatches++;
    else if (approxApproved && !approved)
        approxFalseNegatives++;
    else
        approxFalsePositives++;

    // Production classifier: a bucket's fraud ratio maps to the same approval
    // threshold as a top-5 fraud count.
    bool majorityApproved = hasGroupIndex && groupOffsets[queryGroup + 1] > groupOffsets[queryGroup]
        ? groupFraudCounts[queryGroup] * 2 < groupOffsets[queryGroup + 1] - groupOffsets[queryGroup]
        : approxApproved;
    if (majorityApproved == approved)
        majorityMatches++;
    else if (majorityApproved && !approved)
        majorityFalseNegatives++;
    else
        majorityFalsePositives++;
}

Console.WriteLine($"Results ({numTests} tests):");
Console.WriteLine($"  Correct:        {correct}/{numTests} ({100.0 * correct / numTests:F1}%)");
Console.WriteLine($"  False Positives: {falsePositives}");
Console.WriteLine($"  False Negatives: {falseNegatives}");
Console.WriteLine($"  Avg Latency:     {totalLatencyMs / numTests:F2} ms");
Console.WriteLine($"  Total Time:      {totalLatencyMs:F0} ms");
Console.WriteLine();
Console.WriteLine("Same-group approximate vs exact:");
Console.WriteLine($"  Decision Match:  {approxMatches}/{numTests} ({100.0 * approxMatches / numTests:F1}%)");
Console.WriteLine($"  Approx FP:       {approxFalsePositives}");
Console.WriteLine($"  Approx FN:       {approxFalseNegatives}");
Console.WriteLine($"  Avg Latency:     {approxTotalLatencyMs / numTests:F4} ms");
Console.WriteLine();
Console.WriteLine("Bucket-majority classifier vs exact:");
Console.WriteLine($"  Decision Match:  {majorityMatches}/{numTests} ({100.0 * majorityMatches / numTests:F1}%)");
Console.WriteLine($"  Majority FP:     {majorityFalsePositives}");
Console.WriteLine($"  Majority FN:     {majorityFalseNegatives}");

static int ReadInt32(ReadOnlySpan<byte> span, ref int pos)
{
    int val = MemoryMarshal.Read<int>(span.Slice(pos, 4));
    pos += 4;
    return val;
}

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
