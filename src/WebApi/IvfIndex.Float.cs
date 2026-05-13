/// <summary>
/// IVF2 AVX2 float32 candidate path.
/// </summary>
internal sealed partial class IvfIndex
{
    [SkipLocalsInit]
    private byte FraudCountOnceFloat(
        ReadOnlySpan<short> quantizedQuery,
        int nProbe,
        bool repair,
        long zeroFastApproveWorstDistance,
        long fiveFastDenyWorstDistance)
    {
        Span<int> bestClusters = stackalloc int[nProbe];
        Span<float> bestDistances = stackalloc float[nProbe];
        bestDistances.Fill(float.PositiveInfinity);

        Span<float> candidateDistances = stackalloc float[5];
        Span<int> candidateIds = stackalloc int[5];
        Span<byte> candidateLabels = stackalloc byte[5];
        candidateDistances.Fill(float.PositiveInfinity);
        candidateIds.Fill(int.MaxValue);
        int worstIndex = 0;

        Span<Vector256<float>> queryVectors = stackalloc Vector256<float>[Dims];
        FillQueryFloatVectors(quantizedQuery, queryVectors);

        FindNearestCentroidsFloat(quantizedQuery, queryVectors, bestClusters, bestDistances);

        for (int i = 0; i < nProbe; i++)
        {
            int cluster = bestClusters[i];
            ScanBlocksFloat(candidateDistances, candidateIds, candidateLabels, ref worstIndex, offsets[cluster], offsets[cluster + 1], queryVectors);
        }

        if (repair)
        {
            byte initialFrauds = CountFrauds(candidateLabels);
            float worstDistance = candidateDistances[worstIndex];
            if (initialFrauds == 0 && worstDistance < zeroFastApproveWorstDistance)
                return 0;
            if (initialFrauds == 5 && worstDistance < fiveFastDenyWorstDistance)
                return 5;

            RepairByBoundingBoxFloat(candidateDistances, candidateIds, candidateLabels, ref worstIndex, bestClusters, quantizedQuery, queryVectors);
        }

        return CountFrauds(candidateLabels);
    }

    [SkipLocalsInit]
    private void RepairByBoundingBoxFloat(
        Span<float> candidateDistances,
        Span<int> candidateIds,
        Span<byte> candidateLabels,
        ref int worstIndex,
        scoped ReadOnlySpan<int> probedClusters,
        ReadOnlySpan<short> query,
        scoped ReadOnlySpan<Vector256<float>> queryVectors)
    {
        Span<float> laneDistances = stackalloc float[8];
        Vector256<float> zero = Vector256<float>.Zero;
        int cluster = 0;
        int simdLimit = clusters & ~7;

        for (; cluster < simdLimit; cluster += 8)
        {
            Vector256<float> acc0 = Vector256<float>.Zero;
            Vector256<float> acc1 = Vector256<float>.Zero;
            float worstDistance = candidateDistances[worstIndex];
            Vector256<float> threshold = Vector256.Create(worstDistance);

            AddBoundingBoxDimFloat(cluster, 5, queryVectors, zero, ref acc0);
            AddBoundingBoxDimFloat(cluster, 6, queryVectors, zero, ref acc1);
            AddBoundingBoxDimFloat(cluster, 2, queryVectors, zero, ref acc0);
            AddBoundingBoxDimFloat(cluster, 0, queryVectors, zero, ref acc1);
            AddBoundingBoxDimFloat(cluster, 7, queryVectors, zero, ref acc0);
            AddBoundingBoxDimFloat(cluster, 8, queryVectors, zero, ref acc1);
            AddBoundingBoxDimFloat(cluster, 11, queryVectors, zero, ref acc0);
            AddBoundingBoxDimFloat(cluster, 12, queryVectors, zero, ref acc1);
            Vector256<float> acc = Avx.Add(acc0, acc1);
            if (Avx.MoveMask(Avx.Compare(acc, threshold, FloatComparisonMode.OrderedLessThanOrEqualNonSignaling)) == 0)
                continue;

            AddBoundingBoxDimFloat(cluster, 9, queryVectors, zero, ref acc0);
            AddBoundingBoxDimFloat(cluster, 10, queryVectors, zero, ref acc1);
            AddBoundingBoxDimFloat(cluster, 1, queryVectors, zero, ref acc0);
            AddBoundingBoxDimFloat(cluster, 13, queryVectors, zero, ref acc1);
            AddBoundingBoxDimFloat(cluster, 3, queryVectors, zero, ref acc0);
            AddBoundingBoxDimFloat(cluster, 4, queryVectors, zero, ref acc1);
            acc = Avx.Add(acc0, acc1);
            int passMask = Avx.MoveMask(Avx.Compare(acc, threshold, FloatComparisonMode.OrderedLessThanOrEqualNonSignaling));
            if (passMask == 0)
                continue;

            acc.CopyTo(laneDistances);
            while (passMask != 0)
            {
                int lane = BitOperations.TrailingZeroCount(passMask);
                passMask &= passMask - 1;

                int currentCluster = cluster + lane;
                if (laneDistances[lane] > worstDistance ||
                    offsets[currentCluster] == offsets[currentCluster + 1] ||
                    IsProbed(currentCluster, probedClusters))
                {
                    continue;
                }

                ScanBlocksFloat(candidateDistances, candidateIds, candidateLabels, ref worstIndex, offsets[currentCluster], offsets[currentCluster + 1], queryVectors);
                worstDistance = candidateDistances[worstIndex];
            }
        }

        for (; cluster < clusters; cluster++)
        {
            if (offsets[cluster] == offsets[cluster + 1] ||
                IsProbed(cluster, probedClusters))
                continue;

            if (BoundingBoxCanImproveFloat(cluster, query, candidateDistances[worstIndex]))
                ScanBlocksFloat(candidateDistances, candidateIds, candidateLabels, ref worstIndex, offsets[cluster], offsets[cluster + 1], queryVectors);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddBoundingBoxDimFloat(
        int cluster,
        int dim,
        scoped ReadOnlySpan<Vector256<float>> queryVectors,
        Vector256<float> zero,
        ref Vector256<float> acc)
    {
        ref short minRef = ref bboxMin[dim * clusters + cluster];
        ref short maxRef = ref bboxMax[dim * clusters + cluster];
        Vector256<float> min = Avx2.ConvertToVector256Single(Avx2.ConvertToVector256Int32(Unsafe.ReadUnaligned<Vector128<short>>(ref Unsafe.As<short, byte>(ref minRef))));
        Vector256<float> max = Avx2.ConvertToVector256Single(Avx2.ConvertToVector256Int32(Unsafe.ReadUnaligned<Vector128<short>>(ref Unsafe.As<short, byte>(ref maxRef))));
        Vector256<float> below = Avx.Max(Avx.Subtract(min, queryVectors[dim]), zero);
        Vector256<float> above = Avx.Max(Avx.Subtract(queryVectors[dim], max), zero);
        Vector256<float> gap = Avx.Add(below, above);
        acc = Fma.IsSupported ? Fma.MultiplyAdd(gap, gap, acc) : Avx.Add(acc, Avx.Multiply(gap, gap));
    }

    [SkipLocalsInit]
    private void ScanBlocksFloat(
        Span<float> candidateDistances,
        Span<int> candidateIds,
        Span<byte> candidateLabels,
        ref int worstIndex,
        int startBlock,
        int endBlock,
        scoped ReadOnlySpan<Vector256<float>> queryVectors)
    {
        Span<float> laneDistances = stackalloc float[8];
        for (int block = startBlock; block < endBlock; block++)
        {
            int blockBase = block * Dims * blockLanes;
            int labelBase = block * blockLanes;
            Vector256<float> acc0 = Vector256<float>.Zero;
            Vector256<float> acc1 = Vector256<float>.Zero;
            float worstDistance = candidateDistances[worstIndex];
            Vector256<float> threshold = Vector256.Create(worstDistance);

            AddBlockDimFloat(blockBase, 5, queryVectors, ref acc0);
            AddBlockDimFloat(blockBase, 6, queryVectors, ref acc1);
            AddBlockDimFloat(blockBase, 2, queryVectors, ref acc0);
            AddBlockDimFloat(blockBase, 0, queryVectors, ref acc1);
            AddBlockDimFloat(blockBase, 7, queryVectors, ref acc0);
            AddBlockDimFloat(blockBase, 8, queryVectors, ref acc1);
            AddBlockDimFloat(blockBase, 11, queryVectors, ref acc0);
            AddBlockDimFloat(blockBase, 12, queryVectors, ref acc1);
            Vector256<float> acc = Avx.Add(acc0, acc1);

            int passMask = Avx.MoveMask(Avx.Compare(acc, threshold, FloatComparisonMode.OrderedLessThanOrEqualNonSignaling));
            if (passMask == 0)
                continue;

            AddBlockDimFloat(blockBase, 9, queryVectors, ref acc0);
            AddBlockDimFloat(blockBase, 10, queryVectors, ref acc1);
            AddBlockDimFloat(blockBase, 1, queryVectors, ref acc0);
            AddBlockDimFloat(blockBase, 13, queryVectors, ref acc1);
            AddBlockDimFloat(blockBase, 3, queryVectors, ref acc0);
            AddBlockDimFloat(blockBase, 4, queryVectors, ref acc1);
            acc = Avx.Add(acc0, acc1);

            passMask &= Avx.MoveMask(Avx.Compare(acc, threshold, FloatComparisonMode.OrderedLessThanOrEqualNonSignaling));
            if (passMask == 0)
                continue;

            acc.CopyTo(laneDistances);
            while (passMask != 0)
            {
                int lane = BitOperations.TrailingZeroCount(passMask);
                passMask &= passMask - 1;

                float distance = laneDistances[lane];
                int id = ids[labelBase + lane];
                if (id < 0)
                    continue;

                InsertCandidateFloat(candidateDistances, candidateIds, candidateLabels, ref worstIndex, distance, labels[labelBase + lane], id);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddBlockDimFloat(
        int blockBase,
        int dim,
        scoped ReadOnlySpan<Vector256<float>> queryVectors,
        ref Vector256<float> acc)
    {
        ref short blockRef = ref blocks[blockBase + dim * blockLanes];
        Vector256<float> values = Avx2.ConvertToVector256Single(Avx2.ConvertToVector256Int32(Unsafe.ReadUnaligned<Vector128<short>>(ref Unsafe.As<short, byte>(ref blockRef))));
        Vector256<float> diff = Avx.Subtract(queryVectors[dim], values);
        acc = Fma.IsSupported ? Fma.MultiplyAdd(diff, diff, acc) : Avx.Add(acc, Avx.Multiply(diff, diff));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InsertCandidateFloat(
        Span<float> distances,
        Span<int> ids,
        Span<byte> labels,
        ref int worstIndex,
        float distance,
        byte label,
        int id)
    {
        float worstDistance = distances[worstIndex];
        if (distance > worstDistance || (distance == worstDistance && id >= ids[worstIndex]))
            return;

        distances[worstIndex] = distance;
        ids[worstIndex] = id;
        labels[worstIndex] = label;
        worstIndex = WorstCandidateFloat(distances, ids);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WorstCandidateFloat(ReadOnlySpan<float> distances, ReadOnlySpan<int> ids)
    {
        int worst = 0;
        float worstDistance = distances[0];
        int worstId = ids[0];
        for (int i = 1; i < 5; i++)
        {
            float distance = distances[i];
            int id = ids[i];
            if (distance > worstDistance || (distance == worstDistance && id > worstId))
            {
                worst = i;
                worstDistance = distance;
                worstId = id;
            }
        }

        return worst;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool BoundingBoxCanImproveFloat(int cluster, ReadOnlySpan<short> query, float worstDistance)
    {
        float distance = 0;
        for (int dim = 0; dim < Dims; dim++)
        {
            short value = query[dim];
            short min = bboxMin[dim * clusters + cluster];
            short max = bboxMax[dim * clusters + cluster];
            if (value < min)
            {
                float diff = value - min;
                distance += diff * diff;
                if (distance > worstDistance)
                    return false;
            }
            else if (value > max)
            {
                float diff = value - max;
                distance += diff * diff;
                if (distance > worstDistance)
                    return false;
            }
        }

        return true;
    }

    [SkipLocalsInit]
    private void FindNearestCentroidsFloat(
        ReadOnlySpan<short> query,
        scoped ReadOnlySpan<Vector256<float>> queryVectors,
        Span<int> bestClusters,
        Span<float> bestDistances)
    {
        if (bestClusters.Length == 1)
        {
            FindNearestCentroidFloat(query, queryVectors, bestClusters, bestDistances);
            return;
        }

        Span<float> laneDistances = stackalloc float[8];
        int cluster = 0;
        int simdLimit = clusters & ~7;
        for (; cluster < simdLimit; cluster += 8)
        {
            Vector256<float> acc = Vector256<float>.Zero;
            for (int dim = 0; dim < Dims; dim++)
            {
                ref short centroidRef = ref centroids[dim * clusters + cluster];
                Vector256<float> centroid = Avx2.ConvertToVector256Single(Avx2.ConvertToVector256Int32(Unsafe.ReadUnaligned<Vector128<short>>(ref Unsafe.As<short, byte>(ref centroidRef))));
                Vector256<float> diff = Avx.Subtract(queryVectors[dim], centroid);
                acc = Fma.IsSupported ? Fma.MultiplyAdd(diff, diff, acc) : Avx.Add(acc, Avx.Multiply(diff, diff));
            }

            acc.CopyTo(laneDistances);
            for (int lane = 0; lane < 8; lane++)
                InsertProbeFloat(bestClusters, bestDistances, cluster + lane, laneDistances[lane]);
        }

        for (; cluster < clusters; cluster++)
            InsertProbeFloat(bestClusters, bestDistances, cluster, CentroidDistanceFloat(cluster, query));
    }

    [SkipLocalsInit]
    private void FindNearestCentroidFloat(
        ReadOnlySpan<short> query,
        scoped ReadOnlySpan<Vector256<float>> queryVectors,
        Span<int> bestClusters,
        Span<float> bestDistances)
    {
        Span<float> laneDistances = stackalloc float[8];
        float bestDistance = float.PositiveInfinity;
        int bestCluster = 0;
        int cluster = 0;
        int simdLimit = clusters & ~7;
        for (; cluster < simdLimit; cluster += 8)
        {
            Vector256<float> acc = Vector256<float>.Zero;
            for (int dim = 0; dim < Dims; dim++)
            {
                ref short centroidRef = ref centroids[dim * clusters + cluster];
                Vector256<float> centroid = Avx2.ConvertToVector256Single(Avx2.ConvertToVector256Int32(Unsafe.ReadUnaligned<Vector128<short>>(ref Unsafe.As<short, byte>(ref centroidRef))));
                Vector256<float> diff = Avx.Subtract(queryVectors[dim], centroid);
                acc = Fma.IsSupported ? Fma.MultiplyAdd(diff, diff, acc) : Avx.Add(acc, Avx.Multiply(diff, diff));
            }

            acc.CopyTo(laneDistances);
            for (int lane = 0; lane < 8; lane++)
            {
                float distance = laneDistances[lane];
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestCluster = cluster + lane;
                }
            }
        }

        for (; cluster < clusters; cluster++)
        {
            float distance = CentroidDistanceFloat(cluster, query);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestCluster = cluster;
            }
        }

        bestClusters[0] = bestCluster;
        bestDistances[0] = bestDistance;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float CentroidDistanceFloat(int cluster, ReadOnlySpan<short> query)
    {
        float distance = 0;
        for (int dim = 0; dim < Dims; dim++)
        {
            float diff = query[dim] - centroids[dim * clusters + cluster];
            distance += diff * diff;
        }

        return distance;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InsertProbeFloat(Span<int> clusters, Span<float> distances, int cluster, float distance)
    {
        int last = distances.Length - 1;
        if (distance >= distances[last])
            return;

        int pos = last;
        while (pos > 0 && distance < distances[pos - 1])
        {
            distances[pos] = distances[pos - 1];
            clusters[pos] = clusters[pos - 1];
            pos--;
        }

        distances[pos] = distance;
        clusters[pos] = cluster;
    }

    private static void FillQueryFloatVectors(ReadOnlySpan<short> query, Span<Vector256<float>> queryVectors)
    {
        for (int dim = 0; dim < Dims; dim++)
            queryVectors[dim] = Vector256.Create((float)query[dim]);
    }
}
