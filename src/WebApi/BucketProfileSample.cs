internal readonly record struct BucketProfileSample(
    byte Frauds,
    byte InitialFrauds,
    BucketFastPathStage FastPathStage,
    bool UsedFallback,
    bool UsedExactFallback,
    bool UsedRiskyFallback,
    bool UsedFullRiskyTiebreak,
    int CandidateVisits,
    int ScannedCandidates,
    int SkippedCandidates,
    int RiskyScannedCandidates,
    int ExactScannedCandidates,
    int NeighborBuckets,
    int RiskyFineBuckets)
{
    public bool ProfileFastPath => FastPathStage != BucketFastPathStage.None;
}
