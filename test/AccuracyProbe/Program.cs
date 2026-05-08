if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: AccuracyProbe <test-data.json> <data-dir> [repair-min] [repair-max]");
    Console.Error.WriteLine("       AccuracyProbe <test-data.json> <data-dir> exact <request-id>");
    return 2;
}

string testDataPath = args[0];
string dataDirectory = args[1];

if (args.Length >= 4 && string.Equals(args[2], "exact", StringComparison.OrdinalIgnoreCase))
    return RunExactProbe(testDataPath, dataDirectory, args[3]);

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

static int RunExactProbe(string testDataPath, string dataDirectory, string requestId)
{
    NormalizationConstants normalization = NormalizationConstants.Load(Path.Combine(dataDirectory, "normalization.json"));
    MccRiskTable mcc = MccRiskTable.Load(Path.Combine(dataDirectory, "mcc_risk.json"));
    using JsonDocument testDoc = JsonDocument.Parse(File.ReadAllBytes(testDataPath));

    JsonElement request = default;
    bool expectedApproved = true;
    foreach (JsonElement entry in testDoc.RootElement.GetProperty("entries").EnumerateArray())
    {
        JsonElement candidate = entry.GetProperty("request");
        if (string.Equals(candidate.GetProperty("id").GetString(), requestId, StringComparison.Ordinal))
        {
            request = candidate.Clone();
            expectedApproved = entry.GetProperty("expected_approved").GetBoolean();
            break;
        }
    }

    if (request.ValueKind == JsonValueKind.Undefined)
    {
        Console.Error.WriteLine($"Request not found: {requestId}");
        return 1;
    }

    Span<double> query = stackalloc double[14];
    VectorizeRequestDouble(request, normalization, mcc, query);

    Span<double> distances = stackalloc double[5];
    Span<int> ids = stackalloc int[5];
    Span<byte> labels = stackalloc byte[5];
    distances.Fill(double.PositiveInfinity);
    ids.Fill(int.MaxValue);

    using var stream = File.OpenRead(Path.Combine(dataDirectory, "references.json.gz"));
    using var gzip = new GZipStream(stream, CompressionMode.Decompress);
    using JsonDocument references = JsonDocument.Parse(gzip);

    int row = 0;
    foreach (JsonElement reference in references.RootElement.EnumerateArray())
    {
        JsonElement vector = reference.GetProperty("vector");
        double distance = 0.0;
        for (int dim = 0; dim < 14; dim++)
        {
            double diff = query[dim] - vector[dim].GetDouble();
            distance += diff * diff;
        }

        byte label = reference.GetProperty("label").GetString() == "fraud" ? (byte)1 : (byte)0;
        InsertExact(distances, ids, labels, distance, row, label);
        row++;
    }

    int frauds = FraudCount(labels);
    double fraudScore = frauds / 5.0;
    bool exactApproved = fraudScore < 0.6;

    Console.WriteLine($"request={requestId}");
    Console.WriteLine($"expected_approved={expectedApproved.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}");
    Console.WriteLine($"exact_frauds={frauds}");
    Console.WriteLine($"exact_fraud_score={fraudScore.ToString("0.0", CultureInfo.InvariantCulture)}");
    Console.WriteLine($"exact_approved={exactApproved.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}");
    for (int i = 0; i < 5; i++)
        Console.WriteLine($"top{i + 1}=row:{ids[i]} label:{labels[i]} distance:{distances[i].ToString("R", CultureInfo.InvariantCulture)}");

    return exactApproved == expectedApproved ? 0 : 3;
}

static void VectorizeRequestDouble(JsonElement request, NormalizationConstants normalization, MccRiskTable mcc, Span<double> fv)
{
    JsonElement transaction = request.GetProperty("transaction");
    JsonElement customer = request.GetProperty("customer");
    JsonElement merchant = request.GetProperty("merchant");
    JsonElement terminal = request.GetProperty("terminal");

    double amount = transaction.GetProperty("amount").GetDouble();
    double customerAvgAmount = customer.GetProperty("avg_amount").GetDouble();
    double merchantAvgAmount = merchant.GetProperty("avg_amount").GetDouble();
    FraudVectorizer.ParseIsoUtc(transaction.GetProperty("requested_at").GetString()!, out int hour, out int dayOfWeek, out int requestedMinuteStamp);

    fv[0] = Clamp(amount / normalization.MaxAmount);
    fv[1] = Clamp(transaction.GetProperty("installments").GetInt32() / (double)normalization.MaxInstallments);
    fv[2] = Clamp((amount / customerAvgAmount) / normalization.AmountVsAvgRatio);
    fv[3] = hour / 23.0;
    fv[4] = dayOfWeek / 6.0;

    JsonElement lastTransaction = request.GetProperty("last_transaction");
    if (lastTransaction.ValueKind == JsonValueKind.Null)
    {
        fv[5] = -1.0;
        fv[6] = -1.0;
    }
    else
    {
        FraudVectorizer.ParseIsoUtc(lastTransaction.GetProperty("timestamp").GetString()!, out _, out _, out int lastMinuteStamp);
        fv[5] = Clamp((requestedMinuteStamp - lastMinuteStamp) / (double)normalization.MaxMinutes);
        fv[6] = Clamp(lastTransaction.GetProperty("km_from_current").GetDouble() / normalization.MaxKm);
    }

    string? merchantId = merchant.GetProperty("id").GetString();
    bool known = false;
    foreach (JsonElement knownMerchant in customer.GetProperty("known_merchants").EnumerateArray())
    {
        if (string.Equals(knownMerchant.GetString(), merchantId, StringComparison.Ordinal))
        {
            known = true;
            break;
        }
    }

    int mccCode = int.Parse(merchant.GetProperty("mcc").GetString()!, CultureInfo.InvariantCulture);
    fv[7] = Clamp(terminal.GetProperty("km_from_home").GetDouble() / normalization.MaxKm);
    fv[8] = Clamp(customer.GetProperty("tx_count_24h").GetInt32() / (double)normalization.MaxTxCount24h);
    fv[9] = terminal.GetProperty("is_online").GetBoolean() ? 1.0 : 0.0;
    fv[10] = terminal.GetProperty("card_present").GetBoolean() ? 1.0 : 0.0;
    fv[11] = known ? 0.0 : 1.0;
    fv[12] = mccCode >= 0 && mccCode < mcc.RiskByCode.Length && mcc.KnownByCode[mccCode] ? mcc.RiskByCode[mccCode] : 0.5;
    fv[13] = Clamp(merchantAvgAmount / normalization.MaxMerchantAvgAmount);
}

static void InsertExact(Span<double> distances, Span<int> ids, Span<byte> labels, double distance, int id, byte label)
{
    int last = distances.Length - 1;
    if (distance > distances[last] || (distance == distances[last] && id >= ids[last]))
        return;

    int pos = last;
    while (pos > 0 && (distance < distances[pos - 1] || (distance == distances[pos - 1] && id < ids[pos - 1])))
    {
        distances[pos] = distances[pos - 1];
        ids[pos] = ids[pos - 1];
        labels[pos] = labels[pos - 1];
        pos--;
    }

    distances[pos] = distance;
    ids[pos] = id;
    labels[pos] = label;
}

static int FraudCount(ReadOnlySpan<byte> labels)
{
    int frauds = 0;
    for (int i = 0; i < 5; i++)
    {
        if (labels[i] != 0)
            frauds++;
    }

    return frauds;
}

static double Clamp(double value) => value < 0.0 ? 0.0 : (value > 1.0 ? 1.0 : value);
