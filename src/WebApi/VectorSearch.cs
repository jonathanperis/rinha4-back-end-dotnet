using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

/// <summary>
/// Exact-search helpers for validation modes that compare a query vector against references.
/// </summary>
/// <remarks>
/// The production scorer normally uses fine-bucket majority lookup. These methods
/// remain for correctness experiments and fallback runs where exact top-five
/// nearest-neighbor behavior is required.
/// </remarks>
internal static class VectorSearch
{
    /// <summary>
    /// Scans every packed reference vector with AVX2 and records the five nearest labels.
    /// </summary>
    /// <param name="vecPtr">Pointer to the first packed int16 reference vector.</param>
    /// <param name="labelPtr">Pointer to the one-byte labels aligned by vector index.</param>
    /// <param name="count">Number of reference vectors to scan.</param>
    /// <param name="qPtr">Pointer to the padded int16 query vector.</param>
    /// <param name="top">Mutable top-five tracker updated with closer candidates.</param>
    public static unsafe void SearchAvx2(short* vecPtr, byte* labelPtr, int count, short* qPtr, ref Top5 top)
    {
        const int PaddedDims = 16;
        var qVec = Avx.LoadVector256(qPtr);

        for (int i = 0; i < count; i++)
        {
            short* vPtr = vecPtr + i * PaddedDims;
            var vVec = Avx.LoadVector256(vPtr);

            var diff = Avx2.Subtract(qVec, vVec);
            var (diffLo, diffHi) = Vector256.Widen(diff);
            var sqLo = Avx2.MultiplyLow(diffLo, diffLo);
            var sqHi = Avx2.MultiplyLow(diffHi, diffHi);
            var sum = Avx2.Add(sqLo, sqHi);
            int dist = HorizontalSum256(sum);

            if (dist < top.WorstBound)
                top.TryInsert(dist, labelPtr[i]);
        }
    }

    /// <summary>
    /// Scans packed reference vectors with scalar distance math and early pruning.
    /// </summary>
    /// <param name="vecPtr">Pointer to the first packed int16 reference vector.</param>
    /// <param name="labelPtr">Pointer to the one-byte labels aligned by vector index.</param>
    /// <param name="start">Inclusive vector index where the scan begins.</param>
    /// <param name="end">Exclusive vector index where the scan ends.</param>
    /// <param name="qPtr">Pointer to the padded int16 query vector.</param>
    /// <param name="top">Mutable top-five tracker updated with closer candidates.</param>
    /// <remarks>
    /// Dimension order is tuned by observed selectivity: high-pruning dimensions
    /// run first so bad candidates exit before all 14 dimensions are evaluated.
    /// </remarks>
    public static unsafe void SearchScalarPruned(short* vecPtr, byte* labelPtr, int start, int end, short* qPtr, ref Top5 top)
    {
        const int PaddedDims = 16;
        for (int i = start; i < end; i++)
        {
            short* vPtr = vecPtr + i * PaddedDims;
            int worst = top.WorstBound;
            int dist = 0;
            int diff;

            diff = qPtr[5] - vPtr[5]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
            diff = qPtr[6] - vPtr[6]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
            diff = qPtr[2] - vPtr[2]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
            diff = qPtr[7] - vPtr[7]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
            diff = qPtr[0] - vPtr[0]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
            diff = qPtr[8] - vPtr[8]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
            diff = qPtr[3] - vPtr[3]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
            diff = qPtr[4] - vPtr[4]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
            diff = qPtr[12] - vPtr[12]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
            diff = qPtr[13] - vPtr[13]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
            diff = qPtr[1] - vPtr[1]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
            diff = qPtr[11] - vPtr[11]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
            diff = qPtr[9] - vPtr[9]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;
            diff = qPtr[10] - vPtr[10]; if (!AddSquaredWithinBound(ref dist, diff, worst)) continue;

            if (dist < top.WorstBound)
                top.TryInsert(dist, labelPtr[i]);
        }
    }

    /// <summary>
    /// Adds one squared distance component unless it cannot beat the current top-five bound.
    /// </summary>
    /// <param name="dist">Accumulated squared distance for the candidate vector.</param>
    /// <param name="diff">Signed int16 feature difference widened to int32.</param>
    /// <param name="bound">Current worst retained top-five distance.</param>
    /// <returns><see langword="true"/> when scanning should continue for this candidate.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AddSquaredWithinBound(ref int dist, int diff, int bound)
    {
        int square = diff * diff;
        if (dist >= bound - square)
            return false;

        dist += square;
        return true;
    }

    /// <summary>
    /// Horizontally sums eight int32 lanes from a 256-bit AVX2 distance vector.
    /// </summary>
    /// <param name="value">Vector containing partial squared-distance sums.</param>
    /// <returns>The scalar sum of all eight lanes.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HorizontalSum256(Vector256<int> value)
    {
        var lower = value.GetLower();
        var upper = value.GetUpper();
        var sum128 = Sse2.Add(lower, upper);
        var shuffled = Sse2.Shuffle(sum128, 0b_11_10_11_10);
        var sum64 = Sse2.Add(sum128, shuffled);
        var shuffled2 = Sse2.Shuffle(sum64, 0b_01_00_01_00);
        var sum32 = Sse2.Add(sum64, shuffled2);
        return Sse2.ConvertToInt32(sum32);
    }
}
