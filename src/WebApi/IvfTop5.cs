/// <summary>
/// Stack-only tracker for the five nearest IVF candidates.
/// </summary>
/// <remarks>
/// Distances use <see cref="long"/> because int16 squared distance across 14
/// dimensions can exceed <see cref="int.MaxValue"/> when sentinel values are involved.
/// Original ids break equal-distance ties deterministically.
/// </remarks>
internal ref struct IvfTop5
{
    private long D0, D1, D2, D3, D4;
    private int I0, I1, I2, I3, I4;
    private byte L0, L1, L2, L3, L4;

    /// <summary>
    /// Initializes all slots to empty maximum-distance candidates.
    /// </summary>
    public IvfTop5()
    {
        D0 = D1 = D2 = D3 = D4 = long.MaxValue;
        I0 = I1 = I2 = I3 = I4 = int.MaxValue;
    }

    /// <summary>
    /// Gets the worst retained distance for early pruning and bbox repair.
    /// </summary>
    public readonly long WorstDistance => D4;

    /// <summary>
    /// Attempts to insert a candidate into the sorted top-five list.
    /// </summary>
    /// <param name="distance">Squared int16 Euclidean distance.</param>
    /// <param name="label">Reference label where <c>1</c> means fraud.</param>
    /// <param name="id">Original reference id for deterministic tie-breaking.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TryInsert(long distance, byte label, int id)
    {
        if (!Better(distance, id, D4, I4)) return;

        if (Better(distance, id, D0, I0))
        {
            D4 = D3; I4 = I3; L4 = L3;
            D3 = D2; I3 = I2; L3 = L2;
            D2 = D1; I2 = I1; L2 = L1;
            D1 = D0; I1 = I0; L1 = L0;
            D0 = distance; I0 = id; L0 = label;
        }
        else if (Better(distance, id, D1, I1))
        {
            D4 = D3; I4 = I3; L4 = L3;
            D3 = D2; I3 = I2; L3 = L2;
            D2 = D1; I2 = I1; L2 = L1;
            D1 = distance; I1 = id; L1 = label;
        }
        else if (Better(distance, id, D2, I2))
        {
            D4 = D3; I4 = I3; L4 = L3;
            D3 = D2; I3 = I2; L3 = L2;
            D2 = distance; I2 = id; L2 = label;
        }
        else if (Better(distance, id, D3, I3))
        {
            D4 = D3; I4 = I3; L4 = L3;
            D3 = distance; I3 = id; L3 = label;
        }
        else
        {
            D4 = distance; I4 = id; L4 = label;
        }
    }

    /// <summary>
    /// Counts fraud labels in the retained top-five candidates.
    /// </summary>
    /// <returns>Fraud count from <c>0</c> through <c>5</c>.</returns>
    public readonly byte FraudCount()
    {
        byte count = 0;
        if (L0 != 0) count++;
        if (L1 != 0) count++;
        if (L2 != 0) count++;
        if (L3 != 0) count++;
        if (L4 != 0) count++;
        return count;
    }

    /// <summary>
    /// Compares candidates by distance and original id.
    /// </summary>
    /// <param name="distance">Candidate distance.</param>
    /// <param name="id">Candidate original id.</param>
    /// <param name="currentDistance">Current slot distance.</param>
    /// <param name="currentId">Current slot original id.</param>
    /// <returns><see langword="true"/> when the candidate should rank before the current slot.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Better(long distance, int id, long currentDistance, int currentId) =>
        distance < currentDistance || (distance == currentDistance && id < currentId);
}
