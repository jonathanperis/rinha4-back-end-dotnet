/// <summary>
/// Converts parsed Rinha fraud-score requests into competition responses.
/// </summary>
/// <remarks>
/// The scorer requires one of the rounded int16 indexes produced by
/// <c>DataConverter</c>. Missing or invalid reference data is a startup failure so
/// benchmark runs cannot silently fall back to a weaker classifier.
/// </remarks>
internal sealed class FraudScorer
{
    private const int Dims = 14;
    private const int PaddedDims = 16;
    private const int WarmUpPasses = 4;

    private static readonly byte[][] WarmUpPayloads =
    [
        """
        {"id":"warm-http-1","transaction":{"amount":41.12,"installments":2,"requested_at":"2026-03-11T18:45:53Z"},"customer":{"avg_amount":82.24,"tx_count_24h":3,"known_merchants":["MERC-003","MERC-016"]},"merchant":{"id":"MERC-016","mcc":"5411","avg_amount":60.25},"terminal":{"is_online":false,"card_present":true,"km_from_home":29.23},"last_transaction":null}
        """u8.ToArray(),
        """
        {"id":"warm-http-2","transaction":{"amount":3200.0,"installments":9,"requested_at":"2026-03-17T02:04:06Z"},"customer":{"avg_amount":68.88,"tx_count_24h":18,"known_merchants":["MERC-004","MERC-007","MERC-015"]},"merchant":{"id":"MERC-062","mcc":"7801","avg_amount":25.55},"terminal":{"is_online":true,"card_present":false,"km_from_home":881.61},"last_transaction":{"timestamp":"2026-03-17T01:58:06Z","km_from_current":660.92}}
        """u8.ToArray(),
        """
        {"id":"warm-http-3","transaction":{"amount":384.88,"installments":3,"requested_at":"2026-03-11T20:23:35Z"},"customer":{"avg_amount":769.76,"tx_count_24h":3,"known_merchants":["MERC-009","MERC-001"]},"merchant":{"id":"MERC-001","mcc":"5912","avg_amount":298.95},"terminal":{"is_online":false,"card_present":true,"km_from_home":13.71},"last_transaction":{"timestamp":"2026-03-11T14:58:35Z","km_from_current":18.86}}
        """u8.ToArray()
    ];

    private readonly double maxAmount;
    private readonly int maxInstallments;
    private readonly double amountVsAvgRatio;
    private readonly int maxMinutes;
    private readonly int maxKm;
    private readonly int maxTxCount24h;
    private readonly int maxMerchantAvgAmount;
    private readonly double[] mccRisk;
    private readonly bool[] mccRiskKnown;
    private readonly ExactIndex? exactIndex;
    private readonly IvfIndex? ivfIndex;
    private readonly IvfSearchOptions ivfOptions;
    private readonly BucketIndex? bucketIndex;
    private readonly BucketSearchOptions bucketOptions;
    private readonly ScorerMode mode;

    private FraudScorer(
        NormalizationConstants normalization,
        MccRiskTable mcc,
        ExactIndex? exactIndex,
        IvfIndex? ivfIndex,
        IvfSearchOptions ivfOptions,
        BucketIndex? bucketIndex,
        BucketSearchOptions bucketOptions,
        ScorerMode mode)
    {
        maxAmount = normalization.MaxAmount;
        maxInstallments = normalization.MaxInstallments;
        amountVsAvgRatio = normalization.AmountVsAvgRatio;
        maxMinutes = normalization.MaxMinutes;
        maxKm = normalization.MaxKm;
        maxTxCount24h = normalization.MaxTxCount24h;
        maxMerchantAvgAmount = normalization.MaxMerchantAvgAmount;
        mccRisk = mcc.RiskByCode;
        mccRiskKnown = mcc.KnownByCode;
        this.exactIndex = exactIndex;
        this.ivfIndex = ivfIndex;
        this.ivfOptions = ivfOptions;
        this.bucketIndex = bucketIndex;
        this.bucketOptions = bucketOptions;
        this.mode = mode;
    }

    /// <summary>
    /// Loads normalization constants, MCC risk, and the production scorer index.
    /// </summary>
    /// <param name="dataDirectory">Directory containing generated runtime data files.</param>
    /// <returns>A fully initialized scorer ready to share across raw HTTP connections.</returns>
    public static FraudScorer Load(string dataDirectory)
    {
        NormalizationConstants normalization = NormalizationConstants.Load(Path.Combine(dataDirectory, "normalization.json"));
        MccRiskTable mcc = MccRiskTable.Load(Path.Combine(dataDirectory, "mcc_risk.json"));
        ScorerMode mode = ResolveMode();
        ExactIndex? exactIndex = mode == ScorerMode.Exact ? LoadExact(dataDirectory) : null;
        IvfIndex? ivfIndex = mode == ScorerMode.Ivf ? LoadIvf(dataDirectory) : null;
        BucketIndex? bucketIndex = mode == ScorerMode.Bucket ? LoadBucket(dataDirectory) : null;
        IvfSearchOptions ivfOptions = mode == ScorerMode.Ivf ? IvfSearchOptions.FromEnvironment() : default;
        BucketSearchOptions bucketOptions = mode == ScorerMode.Bucket ? BucketSearchOptions.FromEnvironment() : default;

        Console.WriteLine("Dataset loaded.");
        return new FraudScorer(normalization, mcc, exactIndex, ivfIndex, ivfOptions, bucketIndex, bucketOptions, mode);
    }

    /// <summary>
    /// Scores one fraud-score request body and returns a complete prebuilt HTTP response.
    /// </summary>
    /// <param name="body">UTF-8 JSON request body received from the raw socket buffer.</param>
    /// <returns>
    /// A keep-alive JSON response selected from the six possible fraud-score outputs,
    /// or <see cref="HttpResponses.BadRequest"/> when JSON parsing fails.
    /// </returns>
    /// <remarks>
    /// The method keeps per-request vectors on the stack, quantizes with the same
    /// scale used by <c>DataConverter</c>, and returns one prebuilt response.
    /// </remarks>
    public ReadOnlyMemory<byte> ScoreFraudRequest(ReadOnlySpan<byte> body)
    {
        FraudInput req;
        try
        {
            req = FraudRequestParser.Parse(body);
        }
        catch (JsonException)
        {
            return HttpResponses.BadRequest;
        }

        Span<short> qv = stackalloc short[PaddedDims];
        int scale = mode switch
        {
            ScorerMode.Exact => exactIndex!.Scale,
            ScorerMode.Ivf => ivfIndex!.Scale,
            _ => bucketIndex!.Scale
        };
        QuantizeRequest(req, qv, scale);
        qv[Dims] = 0;
        qv[Dims + 1] = 0;

        byte frauds = mode switch
        {
            ScorerMode.Exact => exactIndex!.FraudCount(qv),
            ScorerMode.Ivf => ivfIndex!.FraudCount(qv, ivfOptions),
            _ => bucketIndex!.FraudCount(qv, bucketOptions)
        };
        return HttpResponses.FraudScores[frauds];
    }

    /// <summary>
    /// Runs representative requests before the socket is exposed so hot scorer pages and branches are ready.
    /// </summary>
    public void WarmUp()
    {
        int checksum = 0;

        for (int pass = 0; pass < WarmUpPasses; pass++)
        {
            foreach (byte[] payload in WarmUpPayloads)
                checksum ^= ScoreFraudRequest(payload).Span[0];
        }

        GC.KeepAlive(checksum);
    }

    private void QuantizeRequest(FraudInput req, Span<short> qv, int scale)
    {
        qv[0] = QuantizeRounded(Clamp(req.Amount / maxAmount), scale);
        qv[1] = QuantizeRounded(Clamp(req.Installments / (double)maxInstallments), scale);
        qv[2] = QuantizeRounded(Clamp((req.Amount / req.CustomerAvgAmount) / amountVsAvgRatio), scale);
        qv[3] = QuantizeRounded(req.Hour / 23.0, scale);
        qv[4] = QuantizeRounded(req.DayOfWeek / 6.0, scale);

        if (req.HasLastTransaction)
        {
            double minutes = (req.RequestedSecondStamp - req.LastSecondStamp) / 60.0;
            qv[5] = QuantizeRounded(Clamp(minutes / maxMinutes), scale);
            qv[6] = QuantizeRounded(Clamp(req.KmFromCurrent / maxKm), scale);
        }
        else
        {
            qv[5] = (short)-scale;
            qv[6] = (short)-scale;
        }

        qv[7] = QuantizeRounded(Clamp(req.KmFromHome / maxKm), scale);
        qv[8] = QuantizeRounded(Clamp(req.TxCount24h / (double)maxTxCount24h), scale);
        qv[9] = req.IsOnline ? (short)scale : (short)0;
        qv[10] = req.CardPresent ? (short)scale : (short)0;
        qv[11] = req.UnknownMerchant ? (short)scale : (short)0;
        qv[12] = QuantizeRounded(req.MccCode >= 0 && req.MccCode < mccRisk.Length && mccRiskKnown[req.MccCode] ? mccRisk[req.MccCode] : 0.5, scale);
        qv[13] = QuantizeRounded(Clamp(req.MerchantAvgAmount / maxMerchantAvgAmount), scale);
    }

    private static ExactIndex LoadExact(string dataDirectory)
    {
        string path = Environment.GetEnvironmentVariable("EXACT_PATH") ?? Path.Combine(dataDirectory, "references.bin");
        if (ExactIndex.TryLoad(path, out ExactIndex? index, out string error) && index is not null)
        {
            Console.WriteLine($"Exact scorer enabled: {path}");
            return index;
        }

        Console.WriteLine($"Exact scorer unavailable. {error}");
        Environment.Exit(1);
        throw new InvalidOperationException(error);
    }

    private static IvfIndex LoadIvf(string dataDirectory)
    {
        string path = Environment.GetEnvironmentVariable("IVF_PATH") ?? Path.Combine(dataDirectory, "references.ivf.bin");
        if (IvfIndex.TryLoad(path, out IvfIndex? index, out string error) && index is not null)
        {
            Console.WriteLine($"IVF scorer enabled: {path}");
            return index;
        }

        Console.WriteLine($"IVF scorer unavailable. {error}");
        Environment.Exit(1);
        throw new InvalidOperationException(error);
    }

    private static BucketIndex LoadBucket(string dataDirectory)
    {
        string path = Environment.GetEnvironmentVariable("BUCKET_PATH") ?? Path.Combine(dataDirectory, "references.bucket.bin");
        if (BucketIndex.TryLoad(path, out BucketIndex? index, out string error) && index is not null)
        {
            Console.WriteLine($"Bucket scorer enabled: {path}");
            return index;
        }

        Console.WriteLine($"Bucket scorer unavailable. {error}");
        Environment.Exit(1);
        throw new InvalidOperationException(error);
    }

    private static ScorerMode ResolveMode()
    {
        string? value = Environment.GetEnvironmentVariable("SCORER_MODE");
        if (string.Equals(value, "exact", StringComparison.OrdinalIgnoreCase))
            return ScorerMode.Exact;
        if (string.Equals(value, "ivf", StringComparison.OrdinalIgnoreCase))
            return ScorerMode.Ivf;

        return ScorerMode.Bucket;
    }

    /// <summary>
    /// Clamps a normalized feature value into the <c>0..1</c> interval.
    /// </summary>
    /// <param name="value">Feature value after division by the normalization denominator.</param>
    /// <returns><paramref name="value"/> capped to the interval accepted by the quantizer.</returns>
    private static double Clamp(double value) => value < 0.0 ? 0.0 : (value > 1.0 ? 1.0 : value);

    /// <summary>
    /// Quantizes a normalized feature for exact search using rounded int16 coordinates.
    /// </summary>
    /// <param name="value">Normalized feature value, including the <c>-1</c> sentinel.</param>
    /// <param name="scale">Dataset quantization scale.</param>
    /// <returns>Rounded int16 coordinate used by the exact scorer.</returns>
    private static short QuantizeRounded(double value, int scale) => (short)Math.Round(value * scale);

    private enum ScorerMode
    {
        Bucket,
        Ivf,
        Exact
    }

}
