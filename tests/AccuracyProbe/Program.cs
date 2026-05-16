if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: AccuracyProbe <test-data.json> <data-dir> [repair-min] [repair-max]");
    Console.Error.WriteLine("       AccuracyProbe <test-data.json> <data-dir> exact <request-id>");
    Console.Error.WriteLine("       AccuracyProbe <test-data.json> <data-dir> profile");
    Console.Error.WriteLine("       AccuracyProbe <test-data.json> <data-dir> hybrid-profile");
    Console.Error.WriteLine("       AccuracyProbe <test-data.json> <data-dir> bucket-profile");
    return 2;
}

string testDataPath = args[0];
string dataDirectory = args[1];

if (args.Length >= 3 && string.Equals(args[2], "profile", StringComparison.OrdinalIgnoreCase))
    return RunProfileProbe(testDataPath, dataDirectory);

if (args.Length >= 3 && string.Equals(args[2], "hybrid-profile", StringComparison.OrdinalIgnoreCase))
    return RunHybridProfileProbe(testDataPath, dataDirectory);

if (args.Length >= 3 && string.Equals(args[2], "bucket-profile", StringComparison.OrdinalIgnoreCase))
    return RunBucketProfileProbe(testDataPath, dataDirectory);

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

static int RunBucketProfileProbe(string testDataPath, string dataDirectory)
{
    NormalizationConstants normalization = NormalizationConstants.Load(Path.Combine(dataDirectory, "normalization.json"));
    MccRiskTable mcc = MccRiskTable.Load(Path.Combine(dataDirectory, "mcc_risk.json"));
    string bucketPath = Environment.GetEnvironmentVariable("BUCKET_PATH") ?? Path.Combine(dataDirectory, "references.bucket.bin");
    if (!BucketIndex.TryLoad(bucketPath, out BucketIndex? index, out string error) || index is null)
    {
        Console.Error.WriteLine(error);
        return 1;
    }

    BucketSearchOptions options = BucketSearchOptions.FromEnvironment();
    using JsonDocument testDoc = JsonDocument.Parse(File.ReadAllBytes(testDataPath));
    var samples = new List<BucketProfileSample>(54100);
    var elapsedNanos = new List<long>(54100);
    var missFraudScores = new SortedDictionary<double, int>();
    var missIds = new List<string>(32);
    var fraudCounts = new int[6];
    var initialFraudCounts = new int[6];
    var fallbackByInitialFrauds = new int[6];
    var initialToFinalFrauds = new int[6, 6];

    int total = 0;
    int fp = 0;
    int fn = 0;
    int tp = 0;
    int tn = 0;
    Span<double> fv = stackalloc double[14];
    Span<short> qv = stackalloc short[16];
    foreach (JsonElement entry in testDoc.RootElement.GetProperty("entries").EnumerateArray())
    {
        bool expectedApproved = entry.GetProperty("expected_approved").GetBoolean();
        JsonElement request = entry.GetProperty("request");
        VectorizeRequestDouble(request, normalization, mcc, fv);
        for (int i = 0; i < 14; i++)
            qv[i] = QuantizeRuntimeRounded(fv[i], index.Scale);
        qv[14] = 0;
        qv[15] = 0;

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        BucketProfileSample sample = index.Profile(qv, options);
        long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start;
        long elapsedNs = elapsed * 1_000_000_000L / System.Diagnostics.Stopwatch.Frequency;
        samples.Add(sample);
        elapsedNanos.Add(elapsedNs);

        bool approved = sample.Frauds < 3;
        double fraudScore = sample.Frauds / 5.0;
        total++;
        fraudCounts[sample.Frauds]++;
        initialFraudCounts[sample.InitialFrauds]++;
        initialToFinalFrauds[sample.InitialFrauds, sample.Frauds]++;
        if (sample.UsedFallback)
            fallbackByInitialFrauds[sample.InitialFrauds]++;
        if (approved == expectedApproved)
        {
            if (approved) tn++;
            else tp++;
            continue;
        }

        string id = request.GetProperty("id").GetString() ?? total.ToString(CultureInfo.InvariantCulture);
        if (approved) fn++;
        else fp++;
        missFraudScores[fraudScore] = missFraudScores.TryGetValue(fraudScore, out int count) ? count + 1 : 1;
        if (missIds.Count < 32)
            missIds.Add($"{id}:{(approved ? "FN" : "FP")}:{fraudScore.ToString(CultureInfo.InvariantCulture)}");
    }

    int weighted = fp + (fn * 3);
    double failureRate = total == 0 ? 0.0 : (fp + fn) * 100.0 / total;
    Console.WriteLine($"mode=bucket-profile early={options.EarlyCandidates} min={options.MinCandidates} max={options.MaxCandidates} avx_cutoff={options.AvxCutoffDims} exact={options.ExactFallback} risky={options.RiskyFallback}");
    Console.WriteLine($"total={total} tp={tp} tn={tn} fp={fp} fn={fn} weighted={weighted} failure_rate={failureRate.ToString("F4", CultureInfo.InvariantCulture)}%");
    Console.WriteLine("initial_fraud_counts=" + string.Join(",", initialFraudCounts.Select(static (count, frauds) => $"{frauds}:{count}")));
    Console.WriteLine("fraud_counts=" + string.Join(",", fraudCounts.Select(static (count, frauds) => $"{frauds}:{count}")));
    PrintFraudMatrix("initial_to_final", initialToFinalFrauds);
    Console.WriteLine("fallback_by_initial=" + string.Join(",", fallbackByInitialFrauds.Select(static (count, frauds) => $"{frauds}:{count}")));
    Console.WriteLine("fast_path_stages=" + FormatStageCounts(Enum.GetValues<BucketFastPathStage>().Select(stage => samples.Count(sample => sample.FastPathStage == stage)).ToArray()));
    Console.WriteLine($"profile_fast_path={samples.Count(static sample => sample.ProfileFastPath)} fallback={samples.Count(static sample => sample.UsedFallback)} risky={samples.Count(static sample => sample.UsedRiskyFallback)} exact={samples.Count(static sample => sample.UsedExactFallback)} full_tiebreak={samples.Count(static sample => sample.UsedFullRiskyTiebreak)} corrections={samples.Count(static sample => sample.InitialFrauds != sample.Frauds)}");
    PrintBucketPercentiles("candidate_visits", samples, static sample => sample.CandidateVisits);
    PrintBucketPercentiles("scanned_candidates", samples, static sample => sample.ScannedCandidates);
    PrintBucketPercentiles("skipped_candidates", samples, static sample => sample.SkippedCandidates);
    PrintBucketPercentiles("neighbor_buckets", samples, static sample => sample.NeighborBuckets);
    PrintBucketPercentiles("risky_scanned", samples, static sample => sample.RiskyScannedCandidates);
    PrintBucketPercentiles("exact_scanned", samples, static sample => sample.ExactScannedCandidates);
    PrintBucketPercentiles("risky_fine_buckets", samples, static sample => sample.RiskyFineBuckets);
    PrintLongPercentiles("elapsed_ns", elapsedNanos);
    Console.WriteLine("miss_fraud_scores=" + string.Join(",", missFraudScores.Select(static item => $"{item.Key.ToString(CultureInfo.InvariantCulture)}:{item.Value}")));
    Console.WriteLine("sample_misses=" + string.Join(",", missIds));
    return 0;
}

static int RunHybridProfileProbe(string testDataPath, string dataDirectory)
{
    NormalizationConstants normalization = NormalizationConstants.Load(Path.Combine(dataDirectory, "normalization.json"));
    MccRiskTable mcc = MccRiskTable.Load(Path.Combine(dataDirectory, "mcc_risk.json"));
    string bucketPath = Environment.GetEnvironmentVariable("BUCKET_PATH") ?? Path.Combine(dataDirectory, "references.bucket.bin");
    if (!BucketIndex.TryLoad(bucketPath, out BucketIndex? bucketIndex, out string error) || bucketIndex is null)
    {
        Console.Error.WriteLine(error);
        return 1;
    }

    ProfileIvfIndex ivfIndex = ProfileIvfIndex.Load(Path.Combine(dataDirectory, "references.ivf.bin"));
    BucketSearchOptions bucketOptions = BucketSearchOptions.FromEnvironment();
    using JsonDocument testDoc = JsonDocument.Parse(File.ReadAllBytes(testDataPath));

    var samples = new List<ProfileSample>(4096);
    var byFrauds = new ProfileBucket[6];
    for (int i = 0; i < byFrauds.Length; i++)
        byFrauds[i] = new ProfileBucket();

    var fastFraudCounts = new int[6];
    var fallbackFraudCounts = new int[6];
    var fallbackInitialCounts = new int[6];
    var initialToFinalFrauds = new int[6, 6];
    var fastPathStages = new int[4];
    var replayFraudDrifts = new int[4];
    var replayApprovalDrifts = new int[4];
    var missFraudScores = new SortedDictionary<double, int>();
    var missIds = new List<string>(32);
    bool replayCascade = EnvBool("CASCADE_REPLAY", false);

    int total = 0;
    int fast = 0;
    int fallback = 0;
    int replayed = 0;
    int fp = 0;
    int fn = 0;
    int tp = 0;
    int tn = 0;
    Span<double> fv = stackalloc double[14];
    Span<short> qv = stackalloc short[16];

    foreach (JsonElement entry in testDoc.RootElement.GetProperty("entries").EnumerateArray())
    {
        bool expectedApproved = entry.GetProperty("expected_approved").GetBoolean();
        JsonElement request = entry.GetProperty("request");
        VectorizeRequestDouble(request, normalization, mcc, fv);
        for (int i = 0; i < 14; i++)
            qv[i] = QuantizeRuntimeRounded(fv[i], bucketIndex.Scale);
        qv[14] = 0;
        qv[15] = 0;

        int frauds;
        if (bucketIndex.TryFastPathFraudCount(qv, bucketOptions, out byte fastFrauds, out BucketFastPathStage stage))
        {
            fast++;
            frauds = fastFrauds;
            fastFraudCounts[frauds]++;
            fastPathStages[(int)stage]++;
            if (replayCascade)
            {
                replayed++;
                ProfileSample replay = ivfIndex.Profile(qv);
                if (replay.Frauds != frauds)
                    replayFraudDrifts[(int)stage]++;
                if ((replay.Frauds < 3) != (frauds < 3))
                    replayApprovalDrifts[(int)stage]++;
            }
        }
        else
        {
            fallback++;
            ProfileSample sample = ivfIndex.Profile(qv);
            samples.Add(sample);
            byFrauds[sample.Frauds].Add(sample);
            fallbackFraudCounts[sample.Frauds]++;
            fallbackInitialCounts[sample.InitialFrauds]++;
            initialToFinalFrauds[sample.InitialFrauds, sample.Frauds]++;
            frauds = sample.Frauds;
        }

        bool approved = frauds < 3;
        total++;
        if (approved == expectedApproved)
        {
            if (approved) tn++;
            else tp++;
            continue;
        }

        string id = request.GetProperty("id").GetString() ?? total.ToString(CultureInfo.InvariantCulture);
        if (approved) fn++;
        else fp++;
        double fraudScore = frauds / 5.0;
        missFraudScores[fraudScore] = missFraudScores.TryGetValue(fraudScore, out int count) ? count + 1 : 1;
        if (missIds.Count < 32)
            missIds.Add($"{id}:{(approved ? "FN" : "FP")}:{fraudScore.ToString(CultureInfo.InvariantCulture)}");
    }

    int weighted = fp + (fn * 3);
    double failureRate = total == 0 ? 0.0 : (fp + fn) * 100.0 / total;
    samples.Sort(static (left, right) => left.TotalBlocks.CompareTo(right.TotalBlocks));
    Console.WriteLine($"mode=hybrid-profile total={total} fast={fast} fallback={fallback} fallback_rate={(fallback * 100.0 / Math.Max(total, 1)).ToString("F2", CultureInfo.InvariantCulture)}% cascade_replay={replayCascade}");
    Console.WriteLine($"total={total} tp={tp} tn={tn} fp={fp} fn={fn} weighted={weighted} failure_rate={failureRate.ToString("F4", CultureInfo.InvariantCulture)}%");
    Console.WriteLine("fast_path_stages=" + FormatStageCounts(fastPathStages));
    if (replayCascade)
    {
        Console.WriteLine($"cascade_replay_total={replayed}");
        Console.WriteLine("cascade_replay_fraud_drifts=" + FormatStageCounts(replayFraudDrifts));
        Console.WriteLine("cascade_replay_approval_drifts=" + FormatStageCounts(replayApprovalDrifts));
    }
    Console.WriteLine("fast_fraud_counts=" + string.Join(",", fastFraudCounts.Select(static (count, frauds) => $"{frauds}:{count}")));
    Console.WriteLine("fallback_initial_counts=" + string.Join(",", fallbackInitialCounts.Select(static (count, frauds) => $"{frauds}:{count}")));
    Console.WriteLine("fallback_fraud_counts=" + string.Join(",", fallbackFraudCounts.Select(static (count, frauds) => $"{frauds}:{count}")));
    PrintFraudMatrix("fallback_initial_to_final", initialToFinalFrauds);
    PrintPercentiles("fallback_repair_clusters", samples, static sample => sample.RepairClusters);
    PrintPercentiles("fallback_repair_blocks", samples, static sample => sample.RepairBlocks);
    PrintPercentiles("fallback_total_blocks", samples, static sample => sample.TotalBlocks);
    PrintEarlyFiveStats(samples);
    PrintInitialDecisionStats(samples);
    for (int frauds = 0; frauds < byFrauds.Length; frauds++)
        Console.WriteLine($"fallback_frauds={frauds} count={byFrauds[frauds].Count} avg_total_blocks={byFrauds[frauds].AverageTotalBlocks():F2} avg_repair_clusters={byFrauds[frauds].AverageRepairClusters():F2}");
    Console.WriteLine("miss_fraud_scores=" + string.Join(",", missFraudScores.Select(static item => $"{item.Key.ToString(CultureInfo.InvariantCulture)}:{item.Value}")));
    Console.WriteLine("sample_misses=" + string.Join(",", missIds));
    return 0;
}

static int RunProfileProbe(string testDataPath, string dataDirectory)
{
    NormalizationConstants normalization = NormalizationConstants.Load(Path.Combine(dataDirectory, "normalization.json"));
    MccRiskTable mcc = MccRiskTable.Load(Path.Combine(dataDirectory, "mcc_risk.json"));
    ProfileIvfIndex index = ProfileIvfIndex.Load(Path.Combine(dataDirectory, "references.ivf.bin"));
    using JsonDocument testDoc = JsonDocument.Parse(File.ReadAllBytes(testDataPath));

    var samples = new List<ProfileSample>(54100);
    var byFrauds = new ProfileBucket[6];
    for (int i = 0; i < byFrauds.Length; i++)
        byFrauds[i] = new ProfileBucket();

    Span<double> fv = stackalloc double[14];
    Span<short> qv = stackalloc short[16];
    foreach (JsonElement entry in testDoc.RootElement.GetProperty("entries").EnumerateArray())
    {
        VectorizeRequestDouble(entry.GetProperty("request"), normalization, mcc, fv);
        for (int i = 0; i < 14; i++)
            qv[i] = QuantizeRounded(fv[i], index.Scale);
        qv[14] = 0;
        qv[15] = 0;

        ProfileSample sample = index.Profile(qv);
        samples.Add(sample);
        byFrauds[sample.Frauds].Add(sample);
    }

    samples.Sort(static (left, right) => left.TotalBlocks.CompareTo(right.TotalBlocks));
    Console.WriteLine($"requests={samples.Count} clusters={index.Clusters} total_index_blocks={index.TotalBlocks}");
    PrintPercentiles("repair_clusters", samples, static sample => sample.RepairClusters);
    PrintPercentiles("repair_blocks", samples, static sample => sample.RepairBlocks);
    PrintPercentiles("total_blocks", samples, static sample => sample.TotalBlocks);
    PrintPercentiles("bbox_checks", samples, static sample => sample.BboxChecks);
    PrintEarlyFiveStats(samples);
    PrintInitialDecisionStats(samples);
    for (int frauds = 0; frauds < byFrauds.Length; frauds++)
        Console.WriteLine($"frauds={frauds} count={byFrauds[frauds].Count} avg_total_blocks={byFrauds[frauds].AverageTotalBlocks():F2} avg_repair_clusters={byFrauds[frauds].AverageRepairClusters():F2}");

    return 0;
}

static void PrintPercentiles(string name, List<ProfileSample> samples, Func<ProfileSample, int> selector)
{
    int[] values = new int[samples.Count];
    for (int i = 0; i < samples.Count; i++)
        values[i] = selector(samples[i]);
    Array.Sort(values);

    Console.WriteLine(
        $"{name}=avg:{values.Average().ToString("F2", CultureInfo.InvariantCulture)} " +
        $"p50:{Percentile(values, 0.50)} p90:{Percentile(values, 0.90)} " +
        $"p95:{Percentile(values, 0.95)} p99:{Percentile(values, 0.99)} max:{values[^1]}");
}

static void PrintBucketPercentiles(string name, List<BucketProfileSample> samples, Func<BucketProfileSample, int> selector)
{
    int[] values = new int[samples.Count];
    for (int i = 0; i < samples.Count; i++)
        values[i] = selector(samples[i]);
    Array.Sort(values);

    Console.WriteLine(
        $"{name}=avg:{values.Average().ToString("F2", CultureInfo.InvariantCulture)} " +
        $"p50:{Percentile(values, 0.50)} p90:{Percentile(values, 0.90)} " +
        $"p95:{Percentile(values, 0.95)} p99:{Percentile(values, 0.99)} max:{values[^1]}");
}

static void PrintLongPercentiles(string name, List<long> values)
{
    long[] sorted = values.ToArray();
    Array.Sort(sorted);

    Console.WriteLine(
        $"{name}=avg:{sorted.Average().ToString("F2", CultureInfo.InvariantCulture)} " +
        $"p50:{PercentileLong(sorted, 0.50)} p90:{PercentileLong(sorted, 0.90)} " +
        $"p95:{PercentileLong(sorted, 0.95)} p99:{PercentileLong(sorted, 0.99)} max:{sorted[^1]}");
}

static string FormatStageCounts(int[] counts)
{
    return string.Join(",", Enum.GetValues<BucketFastPathStage>().Select(stage => $"{stage}:{counts[(int)stage]}"));
}

static bool EnvBool(string name, bool fallback)
{
    string? value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrEmpty(value))
        return fallback;

    return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}

static void PrintFraudMatrix(string name, int[,] matrix)
{
    var rows = new string[6];
    for (int initial = 0; initial < rows.Length; initial++)
    {
        var cells = new string[6];
        for (int final = 0; final < cells.Length; final++)
            cells[final] = $"{final}:{matrix[initial, final]}";
        rows[initial] = $"{initial}->" + string.Join("/", cells);
    }

    Console.WriteLine($"{name}=" + string.Join(",", rows));
}

static int Percentile(int[] sorted, double percentile)
{
    if (sorted.Length == 0)
        return 0;

    int index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
    return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
}

static long PercentileLong(long[] sorted, double percentile)
{
    if (sorted.Length == 0)
        return 0;

    int index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
    return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
}

static void PrintEarlyFiveStats(List<ProfileSample> samples)
{
    long safe = 0;
    long unsafeCount = 0;
    long minSafeWorst = long.MaxValue;
    long maxSafeWorst = 0;
    long minUnsafeWorst = long.MaxValue;
    long maxUnsafeWorst = 0;

    foreach (ProfileSample sample in samples)
    {
        if (sample.InitialFrauds != 5)
            continue;

        if (sample.Frauds == 5)
        {
            safe++;
            minSafeWorst = Math.Min(minSafeWorst, sample.InitialWorstDistance);
            maxSafeWorst = Math.Max(maxSafeWorst, sample.InitialWorstDistance);
        }
        else
        {
            unsafeCount++;
            minUnsafeWorst = Math.Min(minUnsafeWorst, sample.InitialWorstDistance);
            maxUnsafeWorst = Math.Max(maxUnsafeWorst, sample.InitialWorstDistance);
        }
    }

    Console.WriteLine(
        $"initial_five=safe:{safe} unsafe:{unsafeCount} " +
        $"safe_worst:{minSafeWorst}..{maxSafeWorst} unsafe_worst:{minUnsafeWorst}..{maxUnsafeWorst}");
}

static void PrintInitialDecisionStats(List<ProfileSample> samples)
{
    int[] thresholds = [1_000_000, 2_000_000, 3_000_000, 4_000_000, 5_000_000];
    for (int initialFrauds = 0; initialFrauds <= 5; initialFrauds++)
    {
        long approveSafe = 0;
        long approveUnsafe = 0;
        long denySafe = 0;
        long denyUnsafe = 0;
        long approveSafeMax = 0;
        long approveUnsafeMin = long.MaxValue;
        long denySafeMax = 0;
        long denyUnsafeMin = long.MaxValue;

        foreach (ProfileSample sample in samples)
        {
            if (sample.InitialFrauds != initialFrauds)
                continue;

            bool initialApproves = initialFrauds < 3;
            bool finalApproves = sample.Frauds < 3;
            if (initialApproves)
            {
                if (finalApproves)
                {
                    approveSafe++;
                    approveSafeMax = Math.Max(approveSafeMax, sample.InitialWorstDistance);
                }
                else
                {
                    approveUnsafe++;
                    approveUnsafeMin = Math.Min(approveUnsafeMin, sample.InitialWorstDistance);
                }
            }
            else
            {
                if (!finalApproves)
                {
                    denySafe++;
                    denySafeMax = Math.Max(denySafeMax, sample.InitialWorstDistance);
                }
                else
                {
                    denyUnsafe++;
                    denyUnsafeMin = Math.Min(denyUnsafeMin, sample.InitialWorstDistance);
                }
            }
        }

        string approveUnsafeText = approveUnsafe == 0 ? "none" : approveUnsafeMin.ToString(CultureInfo.InvariantCulture);
        string denyUnsafeText = denyUnsafe == 0 ? "none" : denyUnsafeMin.ToString(CultureInfo.InvariantCulture);
        Console.WriteLine(
            $"initial_decision={initialFrauds} approve_safe:{approveSafe} approve_safe_max:{approveSafeMax} " +
            $"approve_unsafe:{approveUnsafe} approve_unsafe_min:{approveUnsafeText} " +
            $"deny_safe:{denySafe} deny_safe_max:{denySafeMax} deny_unsafe:{denyUnsafe} deny_unsafe_min:{denyUnsafeText}");

        foreach (int threshold in thresholds)
        {
            int skipped = 0;
            int bad = 0;
            foreach (ProfileSample sample in samples)
            {
                if (sample.InitialFrauds != initialFrauds || sample.InitialWorstDistance >= threshold)
                    continue;

                skipped++;
                if ((sample.InitialFrauds < 3) != (sample.Frauds < 3))
                    bad++;
            }

            if (skipped > 0 || bad > 0)
                Console.WriteLine($"  threshold<{threshold}: skipped:{skipped} bad:{bad}");
        }
    }
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
    FraudVectorizer.ParseIsoUtc(transaction.GetProperty("requested_at").GetString()!, out int hour, out int dayOfWeek, out int requestedSecondStamp);

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
        FraudVectorizer.ParseIsoUtc(lastTransaction.GetProperty("timestamp").GetString()!, out _, out _, out int lastSecondStamp);
        fv[5] = Clamp(((requestedSecondStamp - lastSecondStamp) / 60.0) / normalization.MaxMinutes);
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

static short QuantizeRuntimeRounded(double value, int scale) => (short)Math.Round(value * scale);

static short QuantizeRounded(double value, int scale) => (short)Math.Round(value * scale, MidpointRounding.AwayFromZero);

internal readonly record struct ProfileSample(
    int Frauds,
    int InitialFrauds,
    long InitialWorstDistance,
    int BboxChecks,
    int RepairClusters,
    int InitialBlocks,
    int RepairBlocks)
{
    public int TotalBlocks => InitialBlocks + RepairBlocks;
}

internal sealed class ProfileBucket
{
    private long totalBlocks;
    private long repairClusters;

    public int Count { get; private set; }

    public void Add(ProfileSample sample)
    {
        Count++;
        totalBlocks += sample.TotalBlocks;
        repairClusters += sample.RepairClusters;
    }

    public double AverageTotalBlocks() => Count == 0 ? 0.0 : totalBlocks / (double)Count;

    public double AverageRepairClusters() => Count == 0 ? 0.0 : repairClusters / (double)Count;
}

internal sealed class ProfileIvfIndex
{
    private const int MagicV2 = 0x32465649;
    private const int Dims = 14;

    private readonly int blockLanes;
    private readonly short[] centroids;
    private readonly short[] bboxMin;
    private readonly short[] bboxMax;
    private readonly int[] offsets;
    private readonly byte[] labels;
    private readonly int[] ids;
    private readonly short[] blocks;

    private ProfileIvfIndex(
        int clusters,
        int scale,
        int blockLanes,
        int totalBlocks,
        short[] centroids,
        short[] bboxMin,
        short[] bboxMax,
        int[] offsets,
        byte[] labels,
        int[] ids,
        short[] blocks)
    {
        Clusters = clusters;
        Scale = scale;
        this.blockLanes = blockLanes;
        TotalBlocks = totalBlocks;
        this.centroids = centroids;
        this.bboxMin = bboxMin;
        this.bboxMax = bboxMax;
        this.offsets = offsets;
        this.labels = labels;
        this.ids = ids;
        this.blocks = blocks;
    }

    public int Clusters { get; }

    public int Scale { get; }

    public int TotalBlocks { get; }

    public static ProfileIvfIndex Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        int magic = reader.ReadInt32();
        if (magic != MagicV2)
            throw new InvalidOperationException("Only IVF2 profile is supported.");

        _ = reader.ReadInt32();
        int clusters = reader.ReadInt32();
        int dims = reader.ReadInt32();
        int scale = reader.ReadInt32();
        int blockLanes = reader.ReadInt32();
        int totalBlocks = reader.ReadInt32();
        if (dims != Dims)
            throw new InvalidOperationException("Unexpected IVF dimensions.");

        int paddedRows = checked(totalBlocks * blockLanes);
        var centroids = new short[checked(clusters * Dims)];
        var bboxMin = new short[checked(clusters * Dims)];
        var bboxMax = new short[checked(clusters * Dims)];
        var offsets = new int[clusters + 1];
        var labels = new byte[paddedRows];
        var ids = new int[paddedRows];
        var blocks = new short[checked(totalBlocks * Dims * blockLanes)];

        ReadArray(stream, centroids);
        ReadArray(stream, bboxMin);
        ReadArray(stream, bboxMax);
        ReadArray(stream, offsets);
        stream.ReadExactly(labels);
        ReadArray(stream, ids);
        ReadArray(stream, blocks);

        return new ProfileIvfIndex(clusters, scale, blockLanes, totalBlocks, centroids, bboxMin, bboxMax, offsets, labels, ids, blocks);
    }

    public ProfileSample Profile(ReadOnlySpan<short> query)
    {
        Span<long> candidateDistances = stackalloc long[5];
        Span<int> candidateIds = stackalloc int[5];
        Span<byte> candidateLabels = stackalloc byte[5];
        candidateDistances.Fill(long.MaxValue);
        candidateIds.Fill(int.MaxValue);

        int bestCluster = 0;
        long bestDistance = long.MaxValue;
        for (int cluster = 0; cluster < Clusters; cluster++)
        {
            long distance = CentroidDistance(cluster, query);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestCluster = cluster;
            }
        }

        int initialBlocks = offsets[bestCluster + 1] - offsets[bestCluster];
        ScanBlocks(candidateDistances, candidateIds, candidateLabels, offsets[bestCluster], offsets[bestCluster + 1], query);
        int initialFrauds = FraudCount(candidateLabels);
        long initialWorstDistance = candidateDistances[^1];

        int bboxChecks = 0;
        int repairClusters = 0;
        int repairBlocks = 0;
        long worstDistance = candidateDistances[^1];
        for (int cluster = 0; cluster < Clusters; cluster++)
        {
            if (cluster == bestCluster || offsets[cluster] == offsets[cluster + 1])
                continue;

            bboxChecks++;
            if (!BoundingBoxCanImprove(cluster, query, worstDistance))
                continue;

            repairClusters++;
            int clusterBlocks = offsets[cluster + 1] - offsets[cluster];
            repairBlocks += clusterBlocks;
            ScanBlocks(candidateDistances, candidateIds, candidateLabels, offsets[cluster], offsets[cluster + 1], query);
            worstDistance = candidateDistances[^1];
        }

        return new ProfileSample(FraudCount(candidateLabels), initialFrauds, initialWorstDistance, bboxChecks, repairClusters, initialBlocks, repairBlocks);
    }

    private static void ReadArray<T>(Stream stream, T[] values) where T : unmanaged =>
        stream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));

    private long CentroidDistance(int cluster, ReadOnlySpan<short> query)
    {
        long distance = 0;
        for (int dim = 0; dim < Dims; dim++)
        {
            int diff = query[dim] - centroids[dim * Clusters + cluster];
            distance += (long)diff * diff;
        }

        return distance;
    }

    private bool BoundingBoxCanImprove(int cluster, ReadOnlySpan<short> query, long worstDistance)
    {
        long distance = 0;
        for (int dim = 0; dim < Dims; dim++)
        {
            short value = query[dim];
            short min = bboxMin[dim * Clusters + cluster];
            short max = bboxMax[dim * Clusters + cluster];
            if (value < min)
            {
                int diff = value - min;
                distance += (long)diff * diff;
                if (distance > worstDistance)
                    return false;
            }
            else if (value > max)
            {
                int diff = value - max;
                distance += (long)diff * diff;
                if (distance > worstDistance)
                    return false;
            }
        }

        return true;
    }

    private void ScanBlocks(
        Span<long> candidateDistances,
        Span<int> candidateIds,
        Span<byte> candidateLabels,
        int startBlock,
        int endBlock,
        ReadOnlySpan<short> query)
    {
        for (int block = startBlock; block < endBlock; block++)
        {
            int blockBase = block * Dims * blockLanes;
            int labelBase = block * blockLanes;
            for (int lane = 0; lane < blockLanes; lane++)
            {
                int id = ids[labelBase + lane];
                if (id < 0)
                    continue;

                long distance = 0;
                for (int dim = 0; dim < Dims; dim++)
                {
                    int diff = query[dim] - blocks[blockBase + dim * blockLanes + lane];
                    distance += (long)diff * diff;
                    if (distance > candidateDistances[^1])
                        break;
                }

                InsertCandidate(candidateDistances, candidateIds, candidateLabels, distance, labels[labelBase + lane], id);
            }
        }
    }

    private static void InsertCandidate(Span<long> distances, Span<int> ids, Span<byte> labels, long distance, byte label, int id)
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

    private static int FraudCount(ReadOnlySpan<byte> labels)
    {
        int frauds = 0;
        for (int i = 0; i < 5; i++)
        {
            if (labels[i] != 0)
                frauds++;
        }

        return frauds;
    }
}
