/// <summary>
/// Converts parsed Rinha fraud-score requests into competition responses.
/// </summary>
/// <remarks>
/// The scorer requires the rounded int16 exact index produced by
/// <c>DataConverter</c>. Missing or invalid reference data is a startup failure so
/// benchmark runs cannot silently fall back to a weaker classifier.
/// </remarks>
internal sealed class FraudScorer
{
    private const int Dims = 14;
    private const int PaddedDims = 16;

    private readonly float maxAmount;
    private readonly int maxInstallments;
    private readonly float amountVsAvgRatio;
    private readonly int maxMinutes;
    private readonly int maxKm;
    private readonly int maxTxCount24h;
    private readonly int maxMerchantAvgAmount;
    private readonly float[] mccRisk;
    private readonly bool[] mccRiskKnown;
    private readonly ExactIndex? exactIndex;
    private readonly IvfIndex? ivfIndex;
    private readonly IvfSearchOptions ivfOptions;
    private readonly BucketIndex? bucketIndex;
    private readonly BucketSearchOptions bucketOptions;
    private readonly bool useExact;
    private readonly bool useIvf;
    private readonly bool useBucket;

    private FraudScorer(
        NormalizationConstants normalization,
        MccRiskTable mcc,
        ExactIndex? exactIndex,
        IvfIndex? ivfIndex,
        IvfSearchOptions ivfOptions,
        BucketIndex? bucketIndex,
        BucketSearchOptions bucketOptions,
        bool useExact,
        bool useIvf,
        bool useBucket)
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
        this.useExact = useExact;
        this.useIvf = useIvf;
        this.useBucket = useBucket;
    }

    /// <summary>
    /// Loads normalization constants, MCC risk, and the production IVF index.
    /// </summary>
    /// <param name="dataDirectory">Directory containing generated runtime data files.</param>
    /// <returns>A fully initialized scorer ready to share across raw HTTP connections.</returns>
    public static FraudScorer Load(string dataDirectory)
    {
        NormalizationConstants normalization = NormalizationConstants.Load(Path.Combine(dataDirectory, "normalization.json"));
        MccRiskTable mcc = MccRiskTable.Load(Path.Combine(dataDirectory, "mcc_risk.json"));
        string scorerMode = Environment.GetEnvironmentVariable("SCORER_MODE") ?? "exact";
        bool useBucket = string.Equals(scorerMode, "bucket", StringComparison.OrdinalIgnoreCase);
        bool useIvf = string.Equals(scorerMode, "ivf", StringComparison.OrdinalIgnoreCase);
        bool useExact = !useBucket && !useIvf;
        ExactIndex? exactIndex = useExact ? LoadExact(dataDirectory) : null;
        IvfIndex? ivfIndex = useIvf ? LoadIvf(dataDirectory) : null;
        BucketIndex? bucketIndex = useBucket ? LoadBucket(dataDirectory) : null;
        IvfSearchOptions ivfOptions = useIvf ? IvfSearchOptions.FromEnvironment() : default;
        BucketSearchOptions bucketOptions = useBucket ? BucketSearchOptions.FromEnvironment() : default;

        Console.WriteLine("Dataset loaded. Ready to serve.");
        return new FraudScorer(normalization, mcc, exactIndex, ivfIndex, ivfOptions, bucketIndex, bucketOptions, useExact, useIvf, useBucket);
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
        int scale = useBucket ? bucketIndex!.Scale : (useIvf ? ivfIndex!.Scale : exactIndex!.Scale);
        QuantizeRequest(req, qv, scale);
        qv[Dims] = 0;
        qv[Dims + 1] = 0;

        byte frauds = useExact
            ? exactIndex!.FraudCount(qv)
            : (useBucket ? bucketIndex!.FraudCount(qv, bucketOptions) : ivfIndex!.FraudCount(qv, ivfOptions));
        return HttpResponses.FraudScores[frauds];
    }

    private void QuantizeRequest(FraudInput req, Span<short> qv, int scale)
    {
        qv[0] = QuantizeRounded(Clamp(req.Amount / maxAmount), scale);
        qv[1] = QuantizeRounded(Clamp(req.Installments / (float)maxInstallments), scale);
        qv[2] = QuantizeRounded(Clamp((req.Amount / req.CustomerAvgAmount) / amountVsAvgRatio), scale);
        qv[3] = QuantizeRounded(req.Hour / 23.0f, scale);
        qv[4] = QuantizeRounded(req.DayOfWeek / 6.0f, scale);

        if (req.HasLastTransaction)
        {
            int minutes = req.RequestedMinuteStamp - req.LastMinuteStamp;
            qv[5] = QuantizeRounded(Clamp(minutes / (float)maxMinutes), scale);
            qv[6] = QuantizeRounded(Clamp(req.KmFromCurrent / maxKm), scale);
        }
        else
        {
            qv[5] = (short)-scale;
            qv[6] = (short)-scale;
        }

        qv[7] = QuantizeRounded(Clamp(req.KmFromHome / maxKm), scale);
        qv[8] = QuantizeRounded(Clamp(req.TxCount24h / (float)maxTxCount24h), scale);
        qv[9] = req.IsOnline ? (short)scale : (short)0;
        qv[10] = req.CardPresent ? (short)scale : (short)0;
        qv[11] = req.UnknownMerchant ? (short)scale : (short)0;
        qv[12] = QuantizeRounded(req.MccCode >= 0 && req.MccCode < mccRisk.Length && mccRiskKnown[req.MccCode] ? mccRisk[req.MccCode] : 0.5f, scale);
        qv[13] = QuantizeRounded(Clamp(req.MerchantAvgAmount / maxMerchantAvgAmount), scale);
    }

    /// <summary>
    /// Loads the required IVF index from <c>IVF_PATH</c> or <paramref name="dataDirectory"/>.
    /// </summary>
    /// <param name="dataDirectory">Directory containing runtime data files.</param>
    /// <returns>The loaded IVF index.</returns>
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

    /// <summary>
    /// Clamps a normalized feature value into the <c>0..1</c> interval.
    /// </summary>
    /// <param name="value">Feature value after division by the normalization denominator.</param>
    /// <returns><paramref name="value"/> capped to the interval accepted by the quantizer.</returns>
    private static float Clamp(float value) => value < 0f ? 0f : (value > 1f ? 1f : value);

    /// <summary>
    /// Quantizes a normalized feature for IVF search using rounded int16 coordinates.
    /// </summary>
    /// <param name="value">Normalized feature value, including the <c>-1</c> sentinel.</param>
    /// <param name="scale">Dataset quantization scale.</param>
    /// <returns>Rounded int16 coordinate used by the IVF scorer.</returns>
    private static short QuantizeRounded(float value, int scale) => (short)MathF.Round(value * scale);

}
