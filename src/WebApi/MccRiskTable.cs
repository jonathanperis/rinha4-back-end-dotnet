/// <summary>
/// Flat MCC risk lookup tables loaded from <c>mcc_risk.json</c>.
/// </summary>
/// <remarks>
/// The presence bitmap is required because a listed MCC can legitimately have
/// risk <c>0.0</c>; missing entries fall back to neutral risk in <see cref="FraudScorer"/>.
/// </remarks>
internal readonly struct MccRiskTable
{
    private const int MccCodeCount = 10000;

    /// <summary>Risk values indexed by numeric MCC code.</summary>
    public readonly double[] RiskByCode;

    /// <summary>Presence bitmap indexed by numeric MCC code.</summary>
    public readonly bool[] KnownByCode;

    /// <summary>
    /// Stores loaded MCC risk and presence arrays.
    /// </summary>
    private MccRiskTable(double[] riskByCode, bool[] knownByCode)
    {
        RiskByCode = riskByCode;
        KnownByCode = knownByCode;
    }

    /// <summary>
    /// Loads the MCC risk JSON into flat arrays for branch-light indexed lookup.
    /// </summary>
    /// <param name="path">Path to <c>mcc_risk.json</c>.</param>
    /// <returns>Loaded MCC risk table.</returns>
    public static MccRiskTable Load(string path)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        var riskByCode = new double[MccCodeCount];
        var knownByCode = new bool[MccCodeCount];

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (int.TryParse(prop.Name, out int code) && code >= 0 && code < riskByCode.Length)
            {
                riskByCode[code] = prop.Value.GetDouble();
                knownByCode[code] = true;
            }
        }

        return new MccRiskTable(riskByCode, knownByCode);
    }
}
