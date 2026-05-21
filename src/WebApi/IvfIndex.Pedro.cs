/// <summary>
/// Pedro-inspired IVF runtime decision path experiments.
/// </summary>
internal sealed partial class IvfIndex
{
    /// <summary>
    /// Runs a Pedro-style two-stage IVF path: a tiny first probe, then an expanded
    /// borderline probe that keeps a wider rerank heap before counting the final
    /// nearest five labels. This stays env-gated so the submitted path is unchanged
    /// unless <c>IVF_PEDRO_DECISION_PATH=1</c> is set.
    /// </summary>
    /// <param name="quantizedQuery">Int16 query vector using the same scale as the index.</param>
    /// <param name="options">Search options containing Pedro-style expansion controls.</param>
    /// <returns>Fraud count from <c>0</c> through <c>5</c>.</returns>
    private byte FraudCountPedroDecisionPath(ReadOnlySpan<short> quantizedQuery, IvfSearchOptions options)
    {
        int fastNProbe = Math.Clamp(options.FastNProbe, 1, clusters);
        bool repairsAllCounts = options.BoundaryFull && options.RepairMinFrauds == 0 && options.RepairMaxFrauds == 5;
        bool fastRepair = options.BboxRepair && (!options.BoundaryFull || repairsAllCounts);
        byte frauds = FraudCountOnceLong(
            quantizedQuery,
            fastNProbe,
            fastRepair,
            options.ZeroFastApproveWorstDistance,
            options.FiveFastDenyWorstDistance);

        bool borderline = frauds >= options.BorderlineMinFrauds && frauds <= options.BorderlineMaxFrauds;
        bool boundaryFull = options.BoundaryFull &&
            !repairsAllCounts &&
            frauds >= options.RepairMinFrauds &&
            frauds <= options.RepairMaxFrauds;

        if (!borderline && !boundaryFull)
            return frauds;

        int expandedNProbe = Math.Clamp(
            Math.Max(Math.Max(options.FullNProbe, options.BorderlineNProbe), fastNProbe),
            1,
            clusters);
        int rerank = Math.Clamp(options.BorderlineRerank, 5, 256);

        return FraudCountOnceLong(
            quantizedQuery,
            expandedNProbe,
            options.BboxRepair,
            options.ZeroFastApproveWorstDistance,
            options.FiveFastDenyWorstDistance,
            rerank);
    }
}
