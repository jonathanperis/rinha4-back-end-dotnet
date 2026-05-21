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

    private readonly double maxAmount;
    private readonly int maxInstallments;
    private readonly double amountVsAvgRatio;
    private readonly int maxMinutes;
    private readonly int maxKm;
    private readonly int maxTxCount24h;
    private readonly int maxMerchantAvgAmount;
    private readonly double[] mccRisk;
    private readonly bool[] mccRiskKnown;
    private readonly short[] mccRiskQuantized;
    private readonly short[] hourQuantized;
    private readonly short[] dayOfWeekQuantized;
    private readonly ExactIndex? exactIndex;
    private readonly IvfIndex? ivfIndex;
    private readonly IvfSearchOptions ivfOptions;
    private readonly BucketIndex? bucketIndex;
    private readonly BucketSearchOptions bucketOptions;
    private readonly ScorerMode mode;
    private readonly int scale;
    private readonly bool submittedHybridFastPath;

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
        scale = mode switch
        {
            ScorerMode.Exact => exactIndex!.Scale,
            ScorerMode.Ivf => ivfIndex!.Scale,
            ScorerMode.Hybrid => bucketIndex!.Scale,
            _ => bucketIndex!.Scale
        };
        mccRiskQuantized = BuildMccRiskQuantized(mccRisk, mccRiskKnown, scale);
        hourQuantized = BuildLinearQuantized(24, 23.0, scale);
        dayOfWeekQuantized = BuildLinearQuantized(7, 6.0, scale);
        submittedHybridFastPath = mode == ScorerMode.Hybrid && !string.Equals(Environment.GetEnvironmentVariable("SUBMITTED_FAST_PATH"), "0", StringComparison.OrdinalIgnoreCase);
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
        IvfIndex? ivfIndex = mode is ScorerMode.Ivf or ScorerMode.Hybrid ? LoadIvf(dataDirectory) : null;
        BucketIndex? bucketIndex = mode is ScorerMode.Bucket or ScorerMode.Hybrid ? LoadBucket(dataDirectory) : null;
        IvfSearchOptions ivfOptions = mode is ScorerMode.Ivf or ScorerMode.Hybrid ? IvfSearchOptions.FromEnvironment() : default;
        BucketSearchOptions bucketOptions = mode is ScorerMode.Bucket or ScorerMode.Hybrid ? BucketSearchOptions.FromEnvironment() : default;
        if (mode == ScorerMode.Hybrid && bucketIndex!.Scale != ivfIndex!.Scale)
            throw new InvalidOperationException($"Hybrid scorer requires matching scales. bucket={bucketIndex.Scale} ivf={ivfIndex.Scale}");

        Console.WriteLine("Dataset loaded. Ready to serve.");
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
    [SkipLocalsInit]
    public ReadOnlyMemory<byte> ScoreFraudRequest(ReadOnlySpan<byte> body)
    {
        Span<short> qv = stackalloc short[PaddedDims];
        try
        {
            QuantizeBody(body, qv);
        }
        catch (JsonException)
        {
            return HttpResponses.BadRequest;
        }

        if (submittedHybridFastPath)
        {
            byte fastFrauds = bucketIndex!.TryFastPathFraudCount(qv, bucketOptions, out byte bucketFrauds)
                ? bucketFrauds
                : ivfIndex!.FraudCount(qv, ivfOptions);
            return HttpResponses.FraudScores[fastFrauds];
        }

        byte frauds = mode switch
        {
            ScorerMode.Exact => exactIndex!.FraudCount(qv),
            ScorerMode.Ivf => ivfIndex!.FraudCount(qv, ivfOptions),
            ScorerMode.Hybrid => bucketIndex!.TryFastPathFraudCount(qv, bucketOptions, out byte fastFrauds) ? fastFrauds : ivfIndex!.FraudCount(qv, ivfOptions),
            _ => bucketIndex!.FraudCount(qv, bucketOptions)
        };
        return HttpResponses.FraudScores[frauds];
    }

    private void QuantizeBody(ReadOnlySpan<byte> body, Span<short> qv)
    {
        if (FraudRequestParser.TryParseOfficialShapeAndQuantize(
                body,
                qv,
                scale,
                maxAmount,
                maxInstallments,
                amountVsAvgRatio,
                maxMinutes,
                maxKm,
                maxTxCount24h,
                maxMerchantAvgAmount,
                mccRiskQuantized,
                hourQuantized,
                dayOfWeekQuantized))
            return;

        FraudInput req = FraudRequestParser.Parse(body);
        QuantizeRequest(req, qv, scale);
        qv[Dims] = 0;
        qv[Dims + 1] = 0;
    }

    private void QuantizeRequest(FraudInput req, Span<short> qv, int scale)
    {
        QuantizeRequestCore(req, qv, scale, maxAmount, maxInstallments, amountVsAvgRatio, maxMinutes, maxKm, maxTxCount24h, maxMerchantAvgAmount, mccRiskQuantized, hourQuantized, dayOfWeekQuantized);
    }

    internal static bool TryParseAndQuantizeForTest(
        ReadOnlySpan<byte> body,
        Span<short> qv,
        int scale,
        double maxAmount,
        int maxInstallments,
        double amountVsAvgRatio,
        int maxMinutes,
        int maxKm,
        int maxTxCount24h,
        int maxMerchantAvgAmount,
        double[] mccRisk,
        bool[] mccRiskKnown)
    {
        try
        {
            FraudInput req = FraudRequestParser.Parse(body);
            QuantizeRequestCore(req, qv, scale, maxAmount, maxInstallments, amountVsAvgRatio, maxMinutes, maxKm, maxTxCount24h, maxMerchantAvgAmount, BuildMccRiskQuantized(mccRisk, mccRiskKnown, scale), BuildLinearQuantized(24, 23.0, scale), BuildLinearQuantized(7, 6.0, scale));
            qv[Dims] = 0;
            qv[Dims + 1] = 0;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static void QuantizeRequestForTest(
        FraudInput req,
        Span<short> qv,
        int scale,
        double maxAmount,
        int maxInstallments,
        double amountVsAvgRatio,
        int maxMinutes,
        int maxKm,
        int maxTxCount24h,
        int maxMerchantAvgAmount,
        double[] mccRisk,
        bool[] mccRiskKnown)
    {
        QuantizeRequestCore(req, qv, scale, maxAmount, maxInstallments, amountVsAvgRatio, maxMinutes, maxKm, maxTxCount24h, maxMerchantAvgAmount, BuildMccRiskQuantized(mccRisk, mccRiskKnown, scale), BuildLinearQuantized(24, 23.0, scale), BuildLinearQuantized(7, 6.0, scale));
        qv[Dims] = 0;
        qv[Dims + 1] = 0;
    }

    private static void QuantizeRequestCore(
        FraudInput req,
        Span<short> qv,
        int scale,
        double maxAmount,
        int maxInstallments,
        double amountVsAvgRatio,
        int maxMinutes,
        int maxKm,
        int maxTxCount24h,
        int maxMerchantAvgAmount,
        short[] mccRiskQuantized,
        short[] hourQuantized,
        short[] dayOfWeekQuantized)
    {
        qv[0] = QuantizeRounded(Clamp(req.Amount / maxAmount), scale);
        qv[1] = QuantizeRounded(Clamp(req.Installments / (double)maxInstallments), scale);
        qv[2] = QuantizeRounded(Clamp((req.Amount / req.CustomerAvgAmount) / amountVsAvgRatio), scale);
        qv[3] = hourQuantized[req.Hour];
        qv[4] = dayOfWeekQuantized[req.DayOfWeek];

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
        qv[12] = req.MccCode >= 0 && req.MccCode < mccRiskQuantized.Length ? mccRiskQuantized[req.MccCode] : QuantizeRounded(0.5, scale);
        qv[13] = QuantizeRounded(Clamp(req.MerchantAvgAmount / maxMerchantAvgAmount), scale);
    }

    private static short[] BuildMccRiskQuantized(double[] mccRisk, bool[] mccRiskKnown, int scale)
    {
        short fallback = QuantizeRounded(0.5, scale);
        var quantized = new short[mccRisk.Length];
        for (int i = 0; i < quantized.Length; i++)
            quantized[i] = i < mccRiskKnown.Length && mccRiskKnown[i] ? QuantizeRounded(mccRisk[i], scale) : fallback;
        return quantized;
    }

    private static short[] BuildLinearQuantized(int count, double divisor, int scale)
    {
        var quantized = new short[count];
        for (int i = 0; i < quantized.Length; i++)
            quantized[i] = QuantizeRounded(i / divisor, scale);
        return quantized;
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
        if (string.Equals(value, "hybrid", StringComparison.OrdinalIgnoreCase))
            return ScorerMode.Hybrid;

        return ScorerMode.Ivf;
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
        Exact,
        Hybrid
    }

}
