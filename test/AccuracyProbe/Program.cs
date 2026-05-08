if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: AccuracyProbe <test-data.json> <data-dir> [repair-min] [repair-max]");
    return 2;
}

string testDataPath = args[0];
string dataDirectory = args[1];
string repairMin = args.Length > 2 ? args[2] : "0";
string repairMax = args.Length > 3 ? args[3] : "5";

Environment.SetEnvironmentVariable("DATA_DIR", dataDirectory);
Environment.SetEnvironmentVariable("IVF_PATH", Path.Combine(dataDirectory, "references.ivf.bin"));
Environment.SetEnvironmentVariable("IVF_FAST_NPROBE", Environment.GetEnvironmentVariable("IVF_FAST_NPROBE") ?? "1");
Environment.SetEnvironmentVariable("IVF_FULL_NPROBE", Environment.GetEnvironmentVariable("IVF_FULL_NPROBE") ?? "1");
Environment.SetEnvironmentVariable("IVF_BOUNDARY_FULL", Environment.GetEnvironmentVariable("IVF_BOUNDARY_FULL") ?? "false");
Environment.SetEnvironmentVariable("IVF_BBOX_REPAIR", Environment.GetEnvironmentVariable("IVF_BBOX_REPAIR") ?? "true");
Environment.SetEnvironmentVariable("IVF_REPAIR_MIN_FRAUDS", repairMin);
Environment.SetEnvironmentVariable("IVF_REPAIR_MAX_FRAUDS", repairMax);

FraudScorer scorer = FraudScorer.Load(dataDirectory);
using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(testDataPath));

int total = 0;
int fp = 0;
int fn = 0;
int tp = 0;
int tn = 0;
var missFraudScores = new SortedDictionary<double, int>();
var missIds = new List<string>(64);

foreach (JsonElement entry in doc.RootElement.GetProperty("entries").EnumerateArray())
{
    bool expectedApproved = entry.GetProperty("expected_approved").GetBoolean();
    string requestJson = entry.GetProperty("request").GetRawText();
    ReadOnlyMemory<byte> response = scorer.ScoreFraudRequest(Encoding.UTF8.GetBytes(requestJson));
    bool approved = ParseApproved(response.Span, out double fraudScore);

    total++;
    if (approved == expectedApproved)
    {
        if (approved) tn++;
        else tp++;
        continue;
    }

    string id = entry.GetProperty("request").GetProperty("id").GetString() ?? total.ToString(CultureInfo.InvariantCulture);
    if (approved) fn++;
    else fp++;

    missFraudScores[fraudScore] = missFraudScores.TryGetValue(fraudScore, out int count) ? count + 1 : 1;
    if (missIds.Count < 32)
        missIds.Add($"{id}:{(approved ? "FN" : "FP")}:{fraudScore.ToString(CultureInfo.InvariantCulture)}");
}

int weighted = fp + (fn * 3);
double failureRate = total == 0 ? 0.0 : (fp + fn) * 100.0 / total;
Console.WriteLine($"mode=ivf repair={repairMin}..{repairMax}");
Console.WriteLine($"total={total} tp={tp} tn={tn} fp={fp} fn={fn} weighted={weighted} failure_rate={failureRate.ToString("F4", CultureInfo.InvariantCulture)}%");
Console.WriteLine("miss_fraud_scores=" + string.Join(",", missFraudScores.Select(static item => $"{item.Key.ToString(CultureInfo.InvariantCulture)}:{item.Value}")));
Console.WriteLine("sample_misses=" + string.Join(",", missIds));
return 0;

static bool ParseApproved(ReadOnlySpan<byte> response, out double fraudScore)
{
    int jsonStart = response.IndexOf((byte)'{');
    if (jsonStart < 0)
        throw new InvalidOperationException("Response body not found.");

    var reader = new Utf8JsonReader(response[jsonStart..]);
    bool approved = false;
    fraudScore = 0.0;
    while (reader.Read())
    {
        if (reader.TokenType != JsonTokenType.PropertyName)
            continue;

        if (reader.ValueTextEquals("approved"u8))
        {
            reader.Read();
            approved = reader.GetBoolean();
        }
        else if (reader.ValueTextEquals("fraud_score"u8))
        {
            reader.Read();
            fraudScore = reader.GetDouble();
        }
    }

    return approved;
}
