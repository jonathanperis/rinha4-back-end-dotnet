/// <summary>
/// Runtime controls for the coarse bucket ANN scorer.
/// </summary>
internal readonly record struct BucketSearchOptions(
    int EarlyCandidates,
    int MinCandidates,
    int MaxCandidates,
    bool ProfileFastPath,
    int ProfileLegitMinCount,
    int ProfileFraudMinCount,
    bool ExactFallback)
{
    public static BucketSearchOptions FromEnvironment()
    {
        int minCandidates = EnvPositiveInt("BUCKET_MIN_CANDIDATES", 16_150);
        int maxCandidates = Math.Max(EnvPositiveInt("BUCKET_MAX_CANDIDATES", 24_200), minCandidates);
        int earlyCandidates = Math.Clamp(EnvPositiveInt("BUCKET_EARLY_CANDIDATES", 9_800), 5, minCandidates);
        int profileMinCount = EnvPositiveInt("BUCKET_PROFILE_MIN_COUNT", 15);

        return new BucketSearchOptions(
            earlyCandidates,
            minCandidates,
            maxCandidates,
            EnvBool("BUCKET_PROFILE_FASTPATH", true),
            EnvPositiveInt("BUCKET_PROFILE_LEGIT_MIN_COUNT", 5),
            EnvPositiveInt("BUCKET_PROFILE_FRAUD_MIN_COUNT", profileMinCount),
            EnvBool("BUCKET_EXACT_FALLBACK", false));
    }

    private static int EnvPositiveInt(string name, int fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, CultureInfo.InvariantCulture, out int parsed) && parsed > 0 ? parsed : fallback;
    }

    private static bool EnvBool(string name, bool fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value))
            return fallback;

        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
