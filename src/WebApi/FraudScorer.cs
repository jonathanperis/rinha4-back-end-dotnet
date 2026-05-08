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

    private readonly double maxAmount;
    private readonly int maxInstallments;
    private readonly double amountVsAvgRatio;
    private readonly int maxMinutes;
    private readonly int maxKm;
    private readonly int maxTxCount24h;
    private readonly int maxMerchantAvgAmount;
    private readonly double[] mccRisk;
    private readonly bool[] mccRiskKnown;
    private readonly ExactIndex exactIndex;

    private FraudScorer(
        NormalizationConstants normalization,
        MccRiskTable mcc,
        ExactIndex exactIndex)
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
    }

    /// <summary>
    /// Loads normalization constants, MCC risk, and the production exact index.
    /// </summary>
    /// <param name="dataDirectory">Directory containing generated runtime data files.</param>
    /// <returns>A fully initialized scorer ready to share across raw HTTP connections.</returns>
    public static FraudScorer Load(string dataDirectory)
    {
        NormalizationConstants normalization = NormalizationConstants.Load(Path.Combine(dataDirectory, "normalization.json"));
        MccRiskTable mcc = MccRiskTable.Load(Path.Combine(dataDirectory, "mcc_risk.json"));
        ExactIndex exactIndex = LoadExact(dataDirectory);

        Console.WriteLine("Dataset loaded. Ready to serve.");
        return new FraudScorer(normalization, mcc, exactIndex);
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
        int scale = exactIndex.Scale;
        QuantizeRequest(req, qv, scale);
        qv[Dims] = 0;
        qv[Dims + 1] = 0;

        byte frauds = exactIndex.FraudCount(qv);
        return HttpResponses.FraudScores[frauds];
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

}
