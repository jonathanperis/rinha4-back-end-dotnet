/// <summary>
/// Runtime controls for the coarse bucket ANN scorer.
/// </summary>
internal readonly record struct BucketSearchOptions(
    int EarlyCandidates,
    int MinCandidates,
    int MaxCandidates,
    bool ReferenceFastPath,
    bool ReferenceFastPathLegit,
    bool ReferenceFastPathFraud,
    bool ReferenceFastPath2Legit,
    bool ReferenceFastPath2Fraud,
    bool ProfileFastPath,
    int ProfileLegitMinCount,
    int ProfileFraudMinCount,
    bool ExactFallback,
    bool RiskyFallback,
    int AvxCutoffDims)
{
    public static BucketSearchOptions FromEnvironment()
    {
        int minCandidates = EnvPositiveInt("BUCKET_MIN_CANDIDATES", 16_150);
        int maxCandidates = Math.Max(EnvPositiveInt("BUCKET_MAX_CANDIDATES", 24_200), minCandidates);
        int earlyCandidates = Math.Clamp(EnvPositiveInt("BUCKET_EARLY_CANDIDATES", 9_800), 5, minCandidates);
        int profileMinCount = EnvPositiveInt("BUCKET_PROFILE_MIN_COUNT", 15);
        int exactFallbackMode = ExactFallbackMode();

        bool referenceLegit = EnvBool("BUCKET_REFERENCE_FASTPATH_LEGIT", false);
        bool referenceFraud = EnvBool("BUCKET_REFERENCE_FASTPATH_FRAUD", true);

        return new BucketSearchOptions(
            earlyCandidates,
            minCandidates,
            maxCandidates,
            EnvBool("BUCKET_REFERENCE_FASTPATH", true),
            referenceLegit,
            referenceFraud,
            EnvBool("BUCKET_REFERENCE_FASTPATH2_LEGIT", true),
            EnvBool("BUCKET_REFERENCE_FASTPATH2_FRAUD", referenceFraud),
            EnvBool("BUCKET_PROFILE_FASTPATH", true),
            EnvPositiveInt("BUCKET_PROFILE_LEGIT_MIN_COUNT", 5),
            EnvPositiveInt("BUCKET_PROFILE_FRAUD_MIN_COUNT", profileMinCount),
            exactFallbackMode != 0,
            exactFallbackMode == 2,
            Math.Clamp(EnvNonNegativeInt("BUCKET_AVX_CUTOFF_DIMS", 6), 0, 8));
    }

    private static int EnvPositiveInt(string name, int fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, CultureInfo.InvariantCulture, out int parsed) && parsed > 0 ? parsed : fallback;
    }

    private static int EnvNonNegativeInt(string name, int fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, CultureInfo.InvariantCulture, out int parsed) && parsed >= 0 ? parsed : fallback;
    }

    private static bool EnvBool(string name, bool fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value))
            return fallback;

        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static int ExactFallbackMode()
    {
        string? value = Environment.GetEnvironmentVariable("BUCKET_EXACT_FALLBACK");
        return value?.ToLowerInvariant() switch
        {
            "1" or "true" or "uncertain" or "exact" => 1,
            "2" or "risky" => 2,
            _ => 0
        };
    }
}
