/// <summary>
/// Converts parsed Rinha fraud-score requests into competition responses.
/// </summary>
/// <remarks>
/// The scorer requires the rounded int16 IVF index produced by
/// <c>DataConverter</c>. Missing or invalid IVF data is a startup failure so
/// benchmark runs cannot silently fall back to a weaker classifier.
/// </remarks>
internal sealed class FraudScorer
{
    private const int Dims = 14;
    private const int PaddedDims = 16;
    private const int Scale = 10000;

    private readonly float maxAmount;
    private readonly int maxInstallments;
    private readonly float amountVsAvgRatio;
    private readonly int maxMinutes;
    private readonly int maxKm;
    private readonly int maxTxCount24h;
    private readonly int maxMerchantAvgAmount;
    private readonly float[] mccRisk;
    private readonly bool[] mccRiskKnown;
    private readonly IvfIndex ivfIndex;
    private readonly IvfSearchOptions ivfOptions;

    /// <summary>
    /// Stores immutable normalization, MCC, and IVF state used by all request handlers.
    /// </summary>
    /// <param name="normalization">Normalization denominators loaded from <c>normalization.json</c>.</param>
    /// <param name="mcc">MCC risk arrays loaded from <c>mcc_risk.json</c>.</param>
    /// <param name="ivfIndex">Loaded production IVF index.</param>
    /// <param name="ivfOptions">Runtime IVF search controls.</param>
    private FraudScorer(
        NormalizationConstants normalization,
        MccRiskTable mcc,
        IvfIndex ivfIndex,
        IvfSearchOptions ivfOptions)
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
        this.ivfIndex = ivfIndex;
        this.ivfOptions = ivfOptions;
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
        IvfIndex ivfIndex = LoadIvf(dataDirectory);
        IvfSearchOptions ivfOptions = IvfSearchOptions.FromEnvironment();

        Console.WriteLine("Dataset loaded. Ready to serve.");
        return new FraudScorer(normalization, mcc, ivfIndex, ivfOptions);
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

        Span<float> fv = stackalloc float[Dims];

        fv[0] = Clamp(req.Amount / maxAmount);
        fv[1] = Clamp(req.Installments / (float)maxInstallments);
        fv[2] = Clamp((req.Amount / req.CustomerAvgAmount) / amountVsAvgRatio);

        int reqMinuteStamp = req.RequestedMinuteStamp;
        fv[3] = req.Hour / 23.0f;
        fv[4] = req.DayOfWeek / 6.0f;

        if (req.HasLastTransaction)
        {
            int minutes = reqMinuteStamp - req.LastMinuteStamp;
            fv[5] = Clamp(minutes / (float)maxMinutes);
            fv[6] = Clamp(req.KmFromCurrent / maxKm);
        }
        else
        {
            fv[5] = -1.0f;
            fv[6] = -1.0f;
        }

        fv[7] = Clamp(req.KmFromHome / maxKm);
        fv[8] = Clamp(req.TxCount24h / (float)maxTxCount24h);
        fv[9] = req.IsOnline ? 1.0f : 0.0f;
        fv[10] = req.CardPresent ? 1.0f : 0.0f;
        fv[11] = req.UnknownMerchant ? 1.0f : 0.0f;
        fv[12] = req.MccCode >= 0 && req.MccCode < mccRisk.Length && mccRiskKnown[req.MccCode] ? mccRisk[req.MccCode] : 0.5f;
        fv[13] = Clamp(req.MerchantAvgAmount / maxMerchantAvgAmount);

        Span<short> qv = stackalloc short[PaddedDims];
        for (int i = 0; i < Dims; i++)
            qv[i] = QuantizeRounded(fv[i], Scale);
        qv[Dims] = 0;
        qv[Dims + 1] = 0;

        byte frauds = ivfIndex.FraudCount(fv, qv, ivfOptions);
        return HttpResponses.FraudScores[frauds];
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
