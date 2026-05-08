/// <summary>
/// IVF3 lower-scale search path using int32 distance accumulation.
/// </summary>
internal sealed partial class IvfIndex
{
    /// <summary>
    /// Runs one IVF pass with optional bounding-box repair.
    /// </summary>
    /// <param name="quantizedQuery">Int16 query vector.</param>
    /// <param name="nProbe">Number of nearest centroid clusters to scan.</param>
    /// <param name="repair">Whether bbox repair may scan additional clusters.</param>
    /// <returns>Fraud count from retained nearest candidates.</returns>
    private byte FraudCountOnce(ReadOnlySpan<short> quantizedQuery, int nProbe, bool repair)
    {
        if (!useInt32Distances)
            return FraudCountOnceLong(quantizedQuery, nProbe, repair);

        Span<int> bestClusters = stackalloc int[nProbe];
        Span<int> bestDistances = stackalloc int[nProbe];
        bestDistances.Fill(int.MaxValue);
        Span<int> candidateDistances = stackalloc int[5];
        Span<int> candidateIds = stackalloc int[5];
        Span<byte> candidateLabels = stackalloc byte[5];
        candidateDistances.Fill(int.MaxValue);
        candidateIds.Fill(int.MaxValue);
        Span<Vector256<int>> queryVectors = stackalloc Vector256<int>[Dims];
        if (Avx2.IsSupported)
            FillQueryVectors(quantizedQuery, queryVectors);

        FindNearestCentroids(quantizedQuery, queryVectors, bestClusters, bestDistances);

        for (int i = 0; i < nProbe; i++)
        {
            int cluster = bestClusters[i];
            ScanBlocks(candidateDistances, candidateIds, candidateLabels, offsets[cluster], offsets[cluster + 1], quantizedQuery, queryVectors);
        }

        if (repair)
            RepairByBoundingBox(candidateDistances, candidateIds, candidateLabels, bestClusters, quantizedQuery, queryVectors);

        return FraudCount(candidateLabels);
    }

    /// <summary>
    /// Scans non-probed clusters whose bounding box could still contain a top-five vector.
    /// </summary>
    /// <param name="candidateDistances">Mutable candidate int32 squared distances.</param>
    /// <param name="candidateIds">Mutable candidate original ids.</param>
    /// <param name="candidateLabels">Mutable candidate labels.</param>
    /// <param name="probedClusters">Clusters already scanned by centroid distance.</param>
    /// <param name="query">Int16 query vector.</param>
    /// <param name="queryVectors">Pre-broadcast AVX2 query vectors, used only when AVX2 is available.</param>
    private void RepairByBoundingBox(
        Span<int> candidateDistances,
        Span<int> candidateIds,
        Span<byte> candidateLabels,
        scoped ReadOnlySpan<int> probedClusters,
        ReadOnlySpan<short> query,
        scoped ReadOnlySpan<Vector256<int>> queryVectors)
    {
        if (Avx2.IsSupported)
        {
            RepairByBoundingBoxAvx2(candidateDistances, candidateIds, candidateLabels, probedClusters, query, queryVectors);
            return;
        }

        int worstDistance = candidateDistances[^1];
        for (int cluster = 0; cluster < clusters; cluster++)
        {
            if (offsets[cluster] == offsets[cluster + 1] ||
                IsProbed(cluster, probedClusters))
                continue;

            if (BoundingBoxCanImprove(cluster, query, worstDistance))
            {
                ScanBlocks(candidateDistances, candidateIds, candidateLabels, offsets[cluster], offsets[cluster + 1], query, queryVectors);
                worstDistance = candidateDistances[^1];
            }
        }
    }

    /// <summary>
    /// Scans bbox lower bounds eight clusters at a time with AVX2.
    /// </summary>
    /// <param name="candidateDistances">Mutable candidate int32 squared distances.</param>
    /// <param name="candidateIds">Mutable candidate original ids.</param>
    /// <param name="candidateLabels">Mutable candidate labels.</param>
    /// <param name="probedClusters">Clusters already scanned by centroid distance.</param>
    /// <param name="query">Int16 query vector.</param>
    /// <param name="queryVectors">Pre-broadcast AVX2 query vectors.</param>
    private void RepairByBoundingBoxAvx2(
        Span<int> candidateDistances,
        Span<int> candidateIds,
        Span<byte> candidateLabels,
        scoped ReadOnlySpan<int> probedClusters,
        ReadOnlySpan<short> query,
        scoped ReadOnlySpan<Vector256<int>> queryVectors)
    {
        Span<int> laneDistances = stackalloc int[8];
        int worstDistance = candidateDistances[^1];
        int cluster = 0;
        int simdLimit = clusters & ~7;
        for (; cluster < simdLimit; cluster += 8)
        {
            Vector256<int> acc = Vector256<int>.Zero;
            for (int dim = 0; dim < Dims; dim++)
            {
                int offset = dim * clusters + cluster;
                ref short minRef = ref bboxMin[offset];
                ref short maxRef = ref bboxMax[offset];
                Vector256<int> min = Avx2.ConvertToVector256Int32(Unsafe.ReadUnaligned<Vector128<short>>(ref Unsafe.As<short, byte>(ref minRef)));
                Vector256<int> max = Avx2.ConvertToVector256Int32(Unsafe.ReadUnaligned<Vector128<short>>(ref Unsafe.As<short, byte>(ref maxRef)));
                Vector256<int> q = queryVectors[dim];
                Vector256<int> below = Avx2.CompareGreaterThan(min, q);
                Vector256<int> above = Avx2.CompareGreaterThan(q, max);
                Vector256<int> belowDiff = Avx2.And(below, Avx2.Subtract(min, q));
                Vector256<int> aboveDiff = Avx2.And(above, Avx2.Subtract(q, max));
                Vector256<int> diff = Avx2.Or(belowDiff, aboveDiff);
                Vector256<int> squared = Avx2.MultiplyLow(diff, diff);
                acc = Avx2.Add(acc, squared);
            }

            acc.CopyTo(laneDistances);

            for (int lane = 0; lane < 8; lane++)
            {
                int laneCluster = cluster + lane;
                if (laneDistances[lane] > worstDistance ||
                    offsets[laneCluster] == offsets[laneCluster + 1] ||
                    IsProbed(laneCluster, probedClusters))
                    continue;

                ScanBlocks(candidateDistances, candidateIds, candidateLabels, offsets[laneCluster], offsets[laneCluster + 1], query, queryVectors);
                worstDistance = candidateDistances[^1];
            }
        }

        for (; cluster < clusters; cluster++)
        {
            if (offsets[cluster] == offsets[cluster + 1] ||
                IsProbed(cluster, probedClusters))
                continue;

            if (BoundingBoxCanImprove(cluster, query, worstDistance))
            {
                ScanBlocks(candidateDistances, candidateIds, candidateLabels, offsets[cluster], offsets[cluster + 1], query, queryVectors);
                worstDistance = candidateDistances[^1];
            }
        }
    }

    /// <summary>
    /// Scans packed blocks and inserts candidates that beat the current top-five bound.
    /// </summary>
    /// <param name="candidateDistances">Mutable candidate int32 squared distances.</param>
    /// <param name="candidateIds">Mutable candidate original ids.</param>
    /// <param name="candidateLabels">Mutable candidate labels.</param>
    /// <param name="startBlock">Inclusive block offset.</param>
    /// <param name="endBlock">Exclusive block offset.</param>
    /// <param name="query">Int16 query vector.</param>
    /// <param name="queryVectors">Pre-broadcast AVX2 query vectors, used only when AVX2 is available.</param>
    private void ScanBlocks(
        Span<int> candidateDistances,
        Span<int> candidateIds,
        Span<byte> candidateLabels,
        int startBlock,
        int endBlock,
        ReadOnlySpan<short> query,
        scoped ReadOnlySpan<Vector256<int>> queryVectors)
    {
        if (Avx2.IsSupported && blockLanes == 8)
        {
            ScanBlocksAvx2(candidateDistances, candidateIds, candidateLabels, startBlock, endBlock, queryVectors);
            return;
        }

        ScanBlocksScalar(candidateDistances, candidateIds, candidateLabels, startBlock, endBlock, query);
    }

    /// <summary>
    /// Scans packed eight-lane blocks with AVX2 int32 squares and accumulation.
    /// </summary>
    /// <param name="candidateDistances">Mutable candidate int32 squared distances.</param>
    /// <param name="candidateIds">Mutable candidate original ids.</param>
    /// <param name="candidateLabels">Mutable candidate labels.</param>
    /// <param name="startBlock">Inclusive block offset.</param>
    /// <param name="endBlock">Exclusive block offset.</param>
    /// <param name="queryVectors">Pre-broadcast AVX2 query vectors.</param>
    private void ScanBlocksAvx2(
        Span<int> candidateDistances,
        Span<int> candidateIds,
        Span<byte> candidateLabels,
        int startBlock,
        int endBlock,
        scoped ReadOnlySpan<Vector256<int>> queryVectors)
    {
        Span<int> laneDistances = stackalloc int[8];
        for (int block = startBlock; block < endBlock; block++)
        {
            int blockBase = block * Dims * blockLanes;
            int labelBase = block * blockLanes;
            Vector256<int> acc = Vector256<int>.Zero;

            for (int dim = 0; dim < Dims; dim++)
            {
                ref short blockRef = ref blocks[blockBase + dim * blockLanes];
                Vector128<short> packedValues = Unsafe.ReadUnaligned<Vector128<short>>(ref Unsafe.As<short, byte>(ref blockRef));
                Vector256<int> values = Avx2.ConvertToVector256Int32(packedValues);
                Vector256<int> diff = Avx2.Subtract(queryVectors[dim], values);
                Vector256<int> squared = Avx2.MultiplyLow(diff, diff);
                acc = Avx2.Add(acc, squared);
            }

            acc.CopyTo(laneDistances);

            int worstDistance = candidateDistances[^1];
            for (int lane = 0; lane < blockLanes; lane++)
            {
                int distance = laneDistances[lane];
                if (distance > worstDistance)
                    continue;

                int id = ids[labelBase + lane];
                if (id < 0)
                    continue;

                InsertCandidate(candidateDistances, candidateIds, candidateLabels, distance, labels[labelBase + lane], id);
                worstDistance = candidateDistances[^1];
            }
        }
    }

    /// <summary>
    /// Scans packed blocks with scalar int32 distances for non-AVX2 runtimes.
    /// </summary>
    /// <param name="candidateDistances">Mutable candidate int32 squared distances.</param>
    /// <param name="candidateIds">Mutable candidate original ids.</param>
    /// <param name="candidateLabels">Mutable candidate labels.</param>
    /// <param name="startBlock">Inclusive block offset.</param>
    /// <param name="endBlock">Exclusive block offset.</param>
    /// <param name="query">Int16 query vector.</param>
    private void ScanBlocksScalar(
        Span<int> candidateDistances,
        Span<int> candidateIds,
        Span<byte> candidateLabels,
        int startBlock,
        int endBlock,
        ReadOnlySpan<short> query)
    {
        for (int block = startBlock; block < endBlock; block++)
        {
            int blockBase = block * Dims * blockLanes;
            int labelBase = block * blockLanes;
            for (int lane = 0; lane < blockLanes; lane++)
            {
                int id = ids[labelBase + lane];
                if (id < 0)
                    continue;

                int distance = 0;
                for (int dim = 0; dim < Dims; dim++)
                {
                    int diff = query[dim] - blocks[blockBase + dim * blockLanes + lane];
                    distance += diff * diff;
                    if (distance > candidateDistances[^1])
                        break;
                }

                InsertCandidate(candidateDistances, candidateIds, candidateLabels, distance, labels[labelBase + lane], id);
            }
        }
    }

    /// <summary>
    /// Counts fraud labels in the first five retained candidates.
    /// </summary>
    /// <param name="candidateLabels">Candidate labels ordered nearest-first.</param>
    /// <returns>Fraud count from <c>0</c> through <c>5</c>.</returns>
    private static byte FraudCount(ReadOnlySpan<byte> candidateLabels)
    {
        byte count = 0;
        int limit = Math.Min(5, candidateLabels.Length);
        for (int i = 0; i < limit; i++)
        {
            if (candidateLabels[i] != 0)
                count++;
        }

        return count;
    }

    /// <summary>
    /// Inserts an int16-ranked candidate into sorted candidate spans.
    /// </summary>
    /// <param name="distances">Sorted candidate distances.</param>
    /// <param name="ids">Sorted candidate ids.</param>
    /// <param name="labels">Sorted candidate labels.</param>
    /// <param name="distance">Candidate int32 squared distance.</param>
    /// <param name="label">Candidate label.</param>
    /// <param name="id">Candidate original id.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InsertCandidate(Span<int> distances, Span<int> ids, Span<byte> labels, int distance, byte label, int id)
    {
        int last = distances.Length - 1;
        if (distance > distances[last] || (distance == distances[last] && id >= ids[last]))
            return;

        int pos = last;
        while (pos > 0 && (distance < distances[pos - 1] || (distance == distances[pos - 1] && id < ids[pos - 1])))
        {
            distances[pos] = distances[pos - 1];
            ids[pos] = ids[pos - 1];
            labels[pos] = labels[pos - 1];
            pos--;
        }

        distances[pos] = distance;
        ids[pos] = id;
        labels[pos] = label;
    }

    /// <summary>
    /// Checks whether a cluster bounding box can still beat the current top-five bound.
    /// </summary>
    /// <param name="cluster">Cluster id.</param>
    /// <param name="query">Int16 query vector.</param>
    /// <param name="worstDistance">Current worst retained top-five distance.</param>
    /// <returns><see langword="true"/> when the bounding-box lower bound does not exceed <paramref name="worstDistance"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool BoundingBoxCanImprove(int cluster, ReadOnlySpan<short> query, int worstDistance)
    {
        int distance = 0;
        for (int dim = 0; dim < Dims; dim++)
        {
            short value = query[dim];
            short min = bboxMin[dim * clusters + cluster];
            short max = bboxMax[dim * clusters + cluster];
            if (value < min)
            {
                int diff = value - min;
                distance += diff * diff;
                if (distance > worstDistance)
                    return false;
            }
            else if (value > max)
            {
                int diff = value - max;
                distance += diff * diff;
                if (distance > worstDistance)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks whether a cluster was already scanned by centroid probing.
    /// </summary>
    /// <param name="cluster">Cluster id.</param>
    /// <param name="probedClusters">Small nprobe cluster list.</param>
    /// <returns><see langword="true"/> when <paramref name="cluster"/> appears in <paramref name="probedClusters"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsProbed(int cluster, ReadOnlySpan<int> probedClusters)
    {
        foreach (int probed in probedClusters)
        {
            if (cluster == probed)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Finds nearest quantized centroids for the query.
    /// </summary>
    /// <param name="query">Int16 query vector.</param>
    /// <param name="bestClusters">Mutable best cluster ids.</param>
    /// <param name="bestDistances">Mutable best centroid distances.</param>
    /// <param name="queryVectors">Pre-broadcast AVX2 query vectors, used only when AVX2 is available.</param>
    private void FindNearestCentroids(
        ReadOnlySpan<short> query,
        scoped ReadOnlySpan<Vector256<int>> queryVectors,
        Span<int> bestClusters,
        Span<int> bestDistances)
    {
        if (Avx2.IsSupported)
        {
            FindNearestCentroidsAvx2(query, queryVectors, bestClusters, bestDistances);
            return;
        }

        for (int cluster = 0; cluster < clusters; cluster++)
            InsertProbe(bestClusters, bestDistances, cluster, CentroidDistance(cluster, query));
    }

    /// <summary>
    /// Finds nearest centroids eight clusters at a time with AVX2.
    /// </summary>
    /// <param name="query">Int16 query vector used for scalar tail clusters.</param>
    /// <param name="queryVectors">Pre-broadcast AVX2 query vectors.</param>
    /// <param name="bestClusters">Mutable best cluster ids.</param>
    /// <param name="bestDistances">Mutable best centroid distances.</param>
    private void FindNearestCentroidsAvx2(
        ReadOnlySpan<short> query,
        scoped ReadOnlySpan<Vector256<int>> queryVectors,
        Span<int> bestClusters,
        Span<int> bestDistances)
    {
        Span<int> laneDistances = stackalloc int[8];
        int cluster = 0;
        int simdLimit = clusters & ~7;
        for (; cluster < simdLimit; cluster += 8)
        {
            Vector256<int> acc = Vector256<int>.Zero;
            for (int dim = 0; dim < Dims; dim++)
            {
                ref short centroidRef = ref centroids[dim * clusters + cluster];
                Vector256<int> centroid = Avx2.ConvertToVector256Int32(Unsafe.ReadUnaligned<Vector128<short>>(ref Unsafe.As<short, byte>(ref centroidRef)));
                Vector256<int> diff = Avx2.Subtract(queryVectors[dim], centroid);
                Vector256<int> squared = Avx2.MultiplyLow(diff, diff);
                acc = Avx2.Add(acc, squared);
            }

            acc.CopyTo(laneDistances);
            for (int lane = 0; lane < 8; lane++)
                InsertProbe(bestClusters, bestDistances, cluster + lane, laneDistances[lane]);
        }

        for (; cluster < clusters; cluster++)
            InsertProbe(bestClusters, bestDistances, cluster, CentroidDistance(cluster, query));
    }

    /// <summary>
    /// Computes quantized centroid distance for one cluster.
    /// </summary>
    /// <param name="cluster">Cluster id.</param>
    /// <param name="query">Int16 query vector.</param>
    /// <returns>Squared int32 distance to the centroid.</returns>
    private int CentroidDistance(int cluster, ReadOnlySpan<short> query)
    {
        int distance = 0;
        for (int dim = 0; dim < Dims; dim++)
        {
            int diff = query[dim] - centroids[dim * clusters + cluster];
            distance += diff * diff;
        }

        return distance;
    }

    /// <summary>
    /// Inserts a centroid into the sorted nprobe list.
    /// </summary>
    /// <param name="clusters">Mutable best cluster ids.</param>
    /// <param name="distances">Mutable best centroid distances.</param>
    /// <param name="cluster">Candidate cluster id.</param>
    /// <param name="distance">Candidate centroid distance.</param>
    private static void InsertProbe(Span<int> clusters, Span<int> distances, int cluster, int distance)
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

}
