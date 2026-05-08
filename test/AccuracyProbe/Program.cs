if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: AccuracyProbe <test-data.json> <data-dir> <bucket|ivf> [repair-min] [repair-max]");
    return 2;
}

string testDataPath = args[0];
string dataDirectory = args[1];
string mode = args[2];

if (string.Equals(mode, "exact", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 4)
    {
        Console.Error.WriteLine("Usage: AccuracyProbe <test-data.json> <data-dir> exact <request-id>");
        return 2;
    }

    return RunExactProbe(testDataPath, dataDirectory, args[3]);
}

string repairMin = args.Length > 3 ? args[3] : "1";
string repairMax = args.Length > 4 ? args[4] : "4";

Environment.SetEnvironmentVariable("SCORER_MODE", mode);
Environment.SetEnvironmentVariable("DATA_PATH", Path.Combine(dataDirectory, "references.bin"));
Environment.SetEnvironmentVariable("IVF_PATH", Path.Combine(dataDirectory, "references.ivf.bin"));
Environment.SetEnvironmentVariable("IVF_FAST_NPROBE", "1");
Environment.SetEnvironmentVariable("IVF_FULL_NPROBE", "1");
Environment.SetEnvironmentVariable("IVF_BOUNDARY_FULL", "true");
Environment.SetEnvironmentVariable("IVF_BBOX_REPAIR", "true");
Environment.SetEnvironmentVariable("IVF_REPAIR_MIN_FRAUDS", repairMin);
Environment.SetEnvironmentVariable("IVF_REPAIR_MAX_FRAUDS", repairMax);

FraudScorer scorer = FraudScorer.Load(Path.Combine(dataDirectory, "references.bin"));
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
Console.WriteLine($"mode={mode} repair={repairMin}..{repairMax}");
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
    Span<int> q8192Distances = stackalloc int[5];
    Span<int> q8192Ids = stackalloc int[5];
    Span<byte> q8192Labels = stackalloc byte[5];
    Span<int> q10000Distances = stackalloc int[5];
    Span<int> q10000Ids = stackalloc int[5];
    Span<byte> q10000Labels = stackalloc byte[5];
    distances.Fill(double.PositiveInfinity);
    ids.Fill(int.MaxValue);
    q8192Distances.Fill(int.MaxValue);
    q8192Ids.Fill(int.MaxValue);
    q10000Distances.Fill(int.MaxValue);
    q10000Ids.Fill(int.MaxValue);
    Span<short> q8192 = stackalloc short[14];
    Span<short> q10000 = stackalloc short[14];
    QuantizeSpan(query, 8192, q8192);
    QuantizeSpan(query, 10000, q10000);

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
        InsertQuantized(q8192Distances, q8192Ids, q8192Labels, QuantizedDistance(q8192, vector, 8192), row, label);
        InsertQuantized(q10000Distances, q10000Ids, q10000Labels, QuantizedDistance(q10000, vector, 10000), row, label);
        row++;
    }

    int frauds = FraudCount(labels);
    int q8192Frauds = FraudCount(q8192Labels);
    int q10000Frauds = FraudCount(q10000Labels);

    Console.WriteLine($"request={requestId} expected_approved={expectedApproved.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()} exact_frauds={frauds} exact_approved={(frauds < 3).ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}");
    for (int i = 0; i < 5; i++)
        Console.WriteLine($"top{i + 1}=row:{ids[i]} label:{labels[i]} distance:{distances[i].ToString("R", CultureInfo.InvariantCulture)}");
    Console.WriteLine($"q8192_frauds={q8192Frauds} q8192_approved={(q8192Frauds < 3).ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}");
    for (int i = 0; i < 5; i++)
        Console.WriteLine($"q8192_top{i + 1}=row:{q8192Ids[i]} label:{q8192Labels[i]} distance:{q8192Distances[i].ToString(CultureInfo.InvariantCulture)}");
    Console.WriteLine($"q10000_frauds={q10000Frauds} q10000_approved={(q10000Frauds < 3).ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}");
    for (int i = 0; i < 5; i++)
        Console.WriteLine($"q10000_top{i + 1}=row:{q10000Ids[i]} label:{q10000Labels[i]} distance:{q10000Distances[i].ToString(CultureInfo.InvariantCulture)}");
    return 0;
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

    fv[0] = ClampDouble(amount / normalization.MaxAmount);
    fv[1] = ClampDouble(transaction.GetProperty("installments").GetInt32() / (double)normalization.MaxInstallments);
    fv[2] = ClampDouble((amount / customerAvgAmount) / normalization.AmountVsAvgRatio);
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
        fv[5] = ClampDouble((requestedMinuteStamp - lastMinuteStamp) / (double)normalization.MaxMinutes);
        fv[6] = ClampDouble(lastTransaction.GetProperty("km_from_current").GetDouble() / normalization.MaxKm);
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
    fv[7] = ClampDouble(terminal.GetProperty("km_from_home").GetDouble() / normalization.MaxKm);
    fv[8] = ClampDouble(customer.GetProperty("tx_count_24h").GetInt32() / (double)normalization.MaxTxCount24h);
    fv[9] = terminal.GetProperty("is_online").GetBoolean() ? 1.0 : 0.0;
    fv[10] = terminal.GetProperty("card_present").GetBoolean() ? 1.0 : 0.0;
    fv[11] = known ? 0.0 : 1.0;
    fv[12] = mccCode >= 0 && mccCode < mcc.RiskByCode.Length && mcc.KnownByCode[mccCode] ? mcc.RiskByCode[mccCode] : 0.5;
    fv[13] = ClampDouble(merchantAvgAmount / normalization.MaxMerchantAvgAmount);
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

static int QuantizedDistance(ReadOnlySpan<short> query, JsonElement vector, int scale)
{
    int distance = 0;
    for (int dim = 0; dim < 14; dim++)
    {
        int diff = query[dim] - QuantizeValue(vector[dim].GetDouble(), scale);
        distance += diff * diff;
    }

    return distance;
}

static void QuantizeSpan(ReadOnlySpan<double> source, int scale, Span<short> destination)
{
    for (int dim = 0; dim < destination.Length; dim++)
        destination[dim] = QuantizeValue(source[dim], scale);
}

static short QuantizeValue(double value, int scale) => (short)Math.Round(value * scale);

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

static void InsertQuantized(Span<int> distances, Span<int> ids, Span<byte> labels, int distance, int id, byte label)
{
    int last = distances.Length - 1;
    if (distance >= distances[last])
        return;

    int pos = last;
    while (pos > 0 && distance < distances[pos - 1])
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

static double ClampDouble(double value) => value < 0.0 ? 0.0 : (value > 1.0 ? 1.0 : value);
