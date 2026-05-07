/// <summary>
/// Converts parsed Rinha fraud-score requests into competition responses.
/// </summary>
/// <remarks>
/// The scorer uses the production <c>O(1)</c> fine-bucket majority lookup built
/// from <c>references.bin</c>. The runtime dataset contains grouped labels and
/// precomputed response indexes, not full reference vectors.
/// </remarks>
internal sealed class FraudScorer
{
    private readonly int dims;
    private readonly int paddedDims;
    private readonly int scale;
    private readonly byte[] groupResponseIndexes;
    private readonly float maxAmount;
    private readonly int maxInstallments;
    private readonly float amountVsAvgRatio;
    private readonly int maxMinutes;
    private readonly int maxKm;
    private readonly int maxTxCount24h;
    private readonly int maxMerchantAvgAmount;
    private readonly float[] mccRisk;
    private readonly bool[] mccRiskKnown;

    /// <summary>
    /// Stores immutable dataset, normalization, and MCC state used by all request handlers.
    /// </summary>
    /// <param name="dataset">Loaded dataset layout and precomputed group response indexes.</param>
    /// <param name="normalization">Normalization denominators loaded from <c>normalization.json</c>.</param>
    /// <param name="mcc">MCC risk arrays loaded from <c>mcc_risk.json</c>.</param>
    private FraudScorer(
        ReferenceDataset dataset,
        NormalizationConstants normalization,
        MccRiskTable mcc)
    {
        dims = dataset.Dims;
        paddedDims = dataset.PaddedDims;
        scale = dataset.Scale;
        groupResponseIndexes = dataset.GroupResponseIndexes;
        maxAmount = normalization.MaxAmount;
        maxInstallments = normalization.MaxInstallments;
        amountVsAvgRatio = normalization.AmountVsAvgRatio;
        maxMinutes = normalization.MaxMinutes;
        maxKm = normalization.MaxKm;
        maxTxCount24h = normalization.MaxTxCount24h;
        maxMerchantAvgAmount = normalization.MaxMerchantAvgAmount;
        mccRisk = mcc.RiskByCode;
        mccRiskKnown = mcc.KnownByCode;
    }

    /// <summary>
    /// Loads <c>references.bin</c>, normalization constants, MCC risk, and bucket-majority responses.
    /// </summary>
    /// <param name="dataPath">Path to <c>references.bin</c>; sibling JSON files are read from the same directory.</param>
    /// <returns>A fully initialized scorer ready to share across raw HTTP connections.</returns>
    public static FraudScorer Load(string dataPath)
    {
        string dataDirectory = Path.GetDirectoryName(dataPath)!;
        ReferenceDataset dataset = ReferenceDataset.Load(dataPath);
        NormalizationConstants normalization = NormalizationConstants.Load(Path.Combine(dataDirectory, "normalization.json"));
        MccRiskTable mcc = MccRiskTable.Load(Path.Combine(dataDirectory, "mcc_risk.json"));

        Console.WriteLine("Dataset loaded. Ready to serve.");
        return new FraudScorer(dataset, normalization, mcc);
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
    /// scale used by <c>DataConverter</c>, and indexes directly into the bucket table.
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

        Span<float> fv = stackalloc float[dims];

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

        Span<short> qv = stackalloc short[paddedDims];
        for (int i = 0; i < dims; i++)
            qv[i] = (short)(fv[i] * scale);
        qv[dims] = 0;
        qv[dims + 1] = 0;

        int queryGroup = FraudVectorizer.FineVectorGroup(qv[5], qv[6], qv[0], qv[7], qv[9], qv[10], qv[11], scale);
        return HttpResponses.FraudScores[groupResponseIndexes[queryGroup]];
    }

    /// <summary>
    /// Clamps a normalized feature value into the <c>0..1</c> interval.
    /// </summary>
    /// <param name="value">Feature value after division by the normalization denominator.</param>
    /// <returns><paramref name="value"/> capped to the interval accepted by the quantizer.</returns>
    private static float Clamp(float value) => value < 0f ? 0f : (value > 1f ? 1f : value);
}
