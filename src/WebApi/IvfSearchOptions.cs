/// <summary>
/// Runtime controls for the IVF scorer mode.
/// </summary>
/// <param name="FastNProbe">Number of nearest centroids scanned on the first pass.</param>
/// <param name="FullNProbe">Number of nearest centroids scanned on the boundary pass.</param>
/// <param name="BoundaryFull">Whether uncertain fraud counts trigger a second pass.</param>
/// <param name="BboxRepair">Whether bounding-box lower bounds may add non-probed clusters.</param>
/// <param name="RepairMinFrauds">Inclusive lower fraud-count boundary for second-pass repair.</param>
/// <param name="RepairMaxFrauds">Inclusive upper fraud-count boundary for second-pass repair.</param>
/// <param name="ZeroFastApproveWorstDistance">Worst top-five distance under which zero-fraud candidates skip repair.</param>
/// <param name="FiveFastDenyWorstDistance">Worst top-five distance under which five-fraud candidates skip repair.</param>
/// <param name="BboxOrdered">Whether to scan clusters directly by bbox lower-bound order.</param>
/// <param name="BboxOrderedMaxProbes">Maximum bbox-ordered clusters to scan before falling back to normal search; zero disables the cap.</param>
/// <param name="PedroDecisionPath">Whether borderline first-pass counts use the Pedro-style expanded IVF path.</param>
/// <param name="BorderlineMinFrauds">Inclusive lower fraud-count boundary for Pedro-style expansion.</param>
/// <param name="BorderlineMaxFrauds">Inclusive upper fraud-count boundary for Pedro-style expansion.</param>
/// <param name="BorderlineNProbe">Number of nearest centroids scanned by the Pedro-style expanded path.</param>
/// <param name="BorderlineRerank">Number of retained candidates during the Pedro-style expanded path.</param>
internal readonly record struct IvfSearchOptions(
    int FastNProbe,
    int FullNProbe,
    bool BoundaryFull,
    bool BboxRepair,
    byte RepairMinFrauds,
    byte RepairMaxFrauds,
    long ZeroFastApproveWorstDistance,
    long FiveFastDenyWorstDistance,
    bool BboxOrdered,
    int BboxOrderedMaxProbes,
    bool PedroDecisionPath,
    byte BorderlineMinFrauds,
    byte BorderlineMaxFrauds,
    int BorderlineNProbe,
    int BorderlineRerank)
{
    /// <summary>
    /// Reads IVF search controls from environment variables.
    /// </summary>
    /// <returns>Search options matching the current experiment defaults.</returns>
    public static IvfSearchOptions FromEnvironment()
    {
        byte repairMin = (byte)Math.Clamp(EnvNonNegativeInt("IVF_REPAIR_MIN_FRAUDS", 0), 0, 5);
        byte repairMax = (byte)Math.Clamp(EnvNonNegativeInt("IVF_REPAIR_MAX_FRAUDS", 5), 0, 5);
        if (repairMin > repairMax)
            (repairMin, repairMax) = (repairMax, repairMin);

        byte borderlineMin = (byte)Math.Clamp(EnvNonNegativeInt("IVF_BORDERLINE_MIN_FRAUDS", 2), 0, 5);
        byte borderlineMax = (byte)Math.Clamp(EnvNonNegativeInt("IVF_BORDERLINE_MAX_FRAUDS", 3), 0, 5);
        if (borderlineMin > borderlineMax)
            (borderlineMin, borderlineMax) = (borderlineMax, borderlineMin);

        return new IvfSearchOptions(
            EnvPositiveInt("IVF_FAST_NPROBE", 1),
            EnvPositiveInt("IVF_FULL_NPROBE", 1),
            EnvBool("IVF_BOUNDARY_FULL", false),
            EnvBool("IVF_BBOX_REPAIR", true),
            repairMin,
            repairMax,
            EnvNonNegativeLong("IVF_ZERO_FAST_APPROVE_WORST_DISTANCE", 0),
            EnvNonNegativeLong("IVF_FIVE_FAST_DENY_WORST_DISTANCE", 0),
            EnvBool("IVF_BBOX_ORDERED", false),
            EnvNonNegativeInt("IVF_BBOX_ORDERED_MAX_PROBES", 0),
            EnvBool("IVF_PEDRO_DECISION_PATH", false),
            borderlineMin,
            borderlineMax,
            EnvPositiveInt("IVF_BORDERLINE_NPROBE", 32),
            EnvPositiveInt("IVF_BORDERLINE_RERANK", 128));
    }

    /// <summary>
    /// Parses an integer environment variable with fallback.
    /// </summary>
    /// <param name="name">Environment variable name.</param>
    /// <param name="fallback">Value used when parsing fails.</param>
    /// <returns>A positive integer value.</returns>
    private static int EnvPositiveInt(string name, int fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, CultureInfo.InvariantCulture, out int parsed) && parsed > 0 ? parsed : fallback;
    }

    /// <summary>
    /// Parses a non-negative integer environment variable with fallback.
    /// </summary>
    /// <param name="name">Environment variable name.</param>
    /// <param name="fallback">Value used when parsing fails.</param>
    /// <returns>A zero-or-positive integer value.</returns>
    private static int EnvNonNegativeInt(string name, int fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, CultureInfo.InvariantCulture, out int parsed) && parsed >= 0 ? parsed : fallback;
    }

    private static long EnvNonNegativeLong(string name, long fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return long.TryParse(value, CultureInfo.InvariantCulture, out long parsed) && parsed >= 0 ? parsed : fallback;
    }

    /// <summary>
    /// Parses a boolean environment variable with fallback.
    /// </summary>
    /// <param name="name">Environment variable name.</param>
    /// <param name="fallback">Value used when the variable is not set.</param>
    /// <returns><see langword="true"/> for <c>1</c> or <c>true</c>.</returns>
    private static bool EnvBool(string name, bool fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value))
            return fallback;

        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
