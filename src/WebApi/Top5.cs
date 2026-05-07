using System.Runtime.CompilerServices;

/// <summary>
/// Stack-only tracker for the five nearest reference labels during exact search.
/// </summary>
/// <remarks>
/// Distances stay sorted in ascending order. <see cref="WorstBound"/> is read
/// by scalar pruning to stop distance accumulation as soon as a candidate cannot
/// enter the top five.
/// </remarks>
internal ref struct Top5
{
    private int D0, D1, D2, D3, D4;
    private byte L0, L1, L2, L3, L4;

    /// <summary>
    /// Initializes all candidate distances to <see cref="int.MaxValue"/>.
    /// </summary>
    public Top5()
    {
        D0 = D1 = D2 = D3 = D4 = int.MaxValue;
    }

    /// <summary>
    /// Gets the largest retained distance, used as the active pruning bound.
    /// </summary>
    /// <value>The fifth-nearest squared distance currently stored.</value>
    public int WorstBound => D4;

    /// <summary>
    /// Inserts a candidate when its squared distance is smaller than the current bound.
    /// </summary>
    /// <param name="dist">Squared distance from query vector to reference vector.</param>
    /// <param name="label">Reference label, where <c>1</c> means fraud and <c>0</c> means non-fraud.</param>
    /// <remarks>
    /// The method uses straight-line slot shifting instead of arrays to avoid
    /// bounds checks and heap allocation in exact-search loops.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TryInsert(int dist, byte label)
    {
        if (dist >= D4) return;

        if (dist < D0)
        {
            D4 = D3; L4 = L3;
            D3 = D2; L3 = L2;
            D2 = D1; L2 = L1;
            D1 = D0; L1 = L0;
            D0 = dist; L0 = label;
        }
        else if (dist < D1)
        {
            D4 = D3; L4 = L3;
            D3 = D2; L3 = L2;
            D2 = D1; L2 = L1;
            D1 = dist; L1 = label;
        }
        else if (dist < D2)
        {
            D4 = D3; L4 = L3;
            D3 = D2; L3 = L2;
            D2 = dist; L2 = label;
        }
        else if (dist < D3)
        {
            D4 = D3; L4 = L3;
            D3 = dist; L3 = label;
        }
        else
        {
            D4 = dist; L4 = label;
        }
    }

    /// <summary>
    /// Counts fraud labels among the five retained nearest candidates.
    /// </summary>
    /// <returns>An integer from 0 through 5 used directly as the response index.</returns>
    public int FraudCount()
    {
        int c = 0;
        if (L0 == 1) c++;
        if (L1 == 1) c++;
        if (L2 == 1) c++;
        if (L3 == 1) c++;
        if (L4 == 1) c++;
        return c;
    }
}
