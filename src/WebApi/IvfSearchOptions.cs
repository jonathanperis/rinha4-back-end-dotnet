/// <summary>
/// Runtime controls for the experimental IVF scorer mode.
/// </summary>
/// <param name="FastNProbe">Number of nearest centroids scanned on the first pass.</param>
/// <param name="FullNProbe">Number of nearest centroids scanned on the boundary pass.</param>
/// <param name="BoundaryFull">Whether uncertain fraud counts trigger a second pass.</param>
/// <param name="BboxRepair">Whether bounding-box lower bounds may add non-probed clusters.</param>
/// <param name="RepairMinFrauds">Inclusive lower fraud-count boundary for second-pass repair.</param>
/// <param name="RepairMaxFrauds">Inclusive upper fraud-count boundary for second-pass repair.</param>
internal readonly record struct IvfSearchOptions(
    int FastNProbe,
    int FullNProbe,
    bool BoundaryFull,
    bool BboxRepair,
    byte RepairMinFrauds,
    byte RepairMaxFrauds)
{
    /// <summary>
    /// Reads IVF search controls from environment variables.
    /// </summary>
    /// <returns>Search options matching the current experiment defaults.</returns>
    public static IvfSearchOptions FromEnvironment() => new(
        EnvInt("IVF_FAST_NPROBE", 1),
        EnvInt("IVF_FULL_NPROBE", 1),
        EnvBool("IVF_BOUNDARY_FULL", true),
        EnvBool("IVF_BBOX_REPAIR", true),
        (byte)Math.Min(5, EnvInt("IVF_REPAIR_MIN_FRAUDS", 1)),
        (byte)Math.Min(5, EnvInt("IVF_REPAIR_MAX_FRAUDS", 4)));

    /// <summary>
    /// Parses an integer environment variable with fallback.
    /// </summary>
    /// <param name="name">Environment variable name.</param>
    /// <param name="fallback">Value used when parsing fails.</param>
    /// <returns>A positive integer value.</returns>
    private static int EnvInt(string name, int fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, CultureInfo.InvariantCulture, out int parsed) && parsed > 0 ? parsed : fallback;
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
