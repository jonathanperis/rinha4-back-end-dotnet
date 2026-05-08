/// <summary>
/// IVF2 candidate search path using int64 distance accumulation.
/// </summary>
internal sealed partial class IvfIndex
{
    /// <summary>
    /// Runs one IVF2-compatible pass with int64 accumulation.
    /// </summary>
    /// <param name="quantizedQuery">Int16 query vector.</param>
    /// <param name="nProbe">Number of nearest centroid clusters to scan.</param>
    /// <param name="repair">Whether bbox repair may scan additional clusters.</param>
    /// <returns>Fraud count from retained nearest candidates.</returns>
    private byte FraudCountOnceLong(ReadOnlySpan<short> quantizedQuery, int nProbe, bool repair)
    {
        Span<int> bestClusters = stackalloc int[nProbe];
        Span<long> bestDistances = stackalloc long[nProbe];
        bestDistances.Fill(long.MaxValue);
        Span<long> candidateDistances = stackalloc long[5];
        Span<int> candidateIds = stackalloc int[5];
        Span<byte> candidateLabels = stackalloc byte[5];
        candidateDistances.Fill(long.MaxValue);
        candidateIds.Fill(int.MaxValue);
        Span<Vector256<int>> queryVectors = stackalloc Vector256<int>[Dims];
        if (Avx2.IsSupported)
            FillQueryVectors(quantizedQuery, queryVectors);

        FindNearestCentroidsLong(quantizedQuery, queryVectors, bestClusters, bestDistances);

        for (int i = 0; i < nProbe; i++)
        {
            int cluster = bestClusters[i];
            ScanBlocksLong(candidateDistances, candidateIds, candidateLabels, offsets[cluster], offsets[cluster + 1], quantizedQuery, queryVectors);
        }

        if (repair)
            RepairByBoundingBoxLong(candidateDistances, candidateIds, candidateLabels, bestClusters, quantizedQuery, queryVectors);

        return FraudCount(candidateLabels);
    }

    /// <summary>
    /// Scans non-probed IVF2 clusters whose bounding box could still contain a top-five vector.
    /// </summary>
    /// <param name="candidateDistances">Mutable candidate int64 squared distances.</param>
    /// <param name="candidateIds">Mutable candidate original ids.</param>
    /// <param name="candidateLabels">Mutable candidate labels.</param>
    /// <param name="probedClusters">Clusters already scanned by centroid distance.</param>
    /// <param name="query">Int16 query vector.</param>
    /// <param name="queryVectors">Pre-broadcast AVX2 query vectors, used only when AVX2 is available.</param>
    private void RepairByBoundingBoxLong(
        Span<long> candidateDistances,
        Span<int> candidateIds,
        Span<byte> candidateLabels,
        scoped ReadOnlySpan<int> probedClusters,
        ReadOnlySpan<short> query,
        scoped ReadOnlySpan<Vector256<int>> queryVectors)
    {
        long worstDistance = candidateDistances[^1];
        for (int cluster = 0; cluster < clusters; cluster++)
        {
            if (offsets[cluster] == offsets[cluster + 1] ||
                IsProbed(cluster, probedClusters))
                continue;

            if (BoundingBoxCanImproveLong(cluster, query, worstDistance))
            {
                ScanBlocksLong(candidateDistances, candidateIds, candidateLabels, offsets[cluster], offsets[cluster + 1], query, queryVectors);
                worstDistance = candidateDistances[^1];
            }
        }
    }

    /// <summary>
    /// Scans IVF2 packed blocks and inserts candidates that beat the current top-five bound.
    /// </summary>
    /// <param name="candidateDistances">Mutable candidate int64 squared distances.</param>
    /// <param name="candidateIds">Mutable candidate original ids.</param>
    /// <param name="candidateLabels">Mutable candidate labels.</param>
    /// <param name="startBlock">Inclusive block offset.</param>
    /// <param name="endBlock">Exclusive block offset.</param>
    /// <param name="query">Int16 query vector.</param>
    /// <param name="queryVectors">Pre-broadcast AVX2 query vectors, used only when AVX2 is available.</param>
    private void ScanBlocksLong(
        Span<long> candidateDistances,
        Span<int> candidateIds,
        Span<byte> candidateLabels,
        int startBlock,
        int endBlock,
        ReadOnlySpan<short> query,
        scoped ReadOnlySpan<Vector256<int>> queryVectors)
    {
        if (Avx2.IsSupported && blockLanes == 8)
        {
            ScanBlocksAvx2Long(candidateDistances, candidateIds, candidateLabels, startBlock, endBlock, queryVectors);
            return;
        }

        ScanBlocksScalarLong(candidateDistances, candidateIds, candidateLabels, startBlock, endBlock, query);
    }

    /// <summary>
    /// Scans IVF2 eight-lane blocks with AVX2 int32 squares and int64 accumulation.
    /// </summary>
    /// <param name="candidateDistances">Mutable candidate int64 squared distances.</param>
    /// <param name="candidateIds">Mutable candidate original ids.</param>
    /// <param name="candidateLabels">Mutable candidate labels.</param>
    /// <param name="startBlock">Inclusive block offset.</param>
    /// <param name="endBlock">Exclusive block offset.</param>
    /// <param name="queryVectors">Pre-broadcast AVX2 query vectors.</param>
    private void ScanBlocksAvx2Long(
        Span<long> candidateDistances,
        Span<int> candidateIds,
        Span<byte> candidateLabels,
        int startBlock,
        int endBlock,
        scoped ReadOnlySpan<Vector256<int>> queryVectors)
    {
        Span<long> laneDistances = stackalloc long[8];
        for (int block = startBlock; block < endBlock; block++)
        {
            int blockBase = block * Dims * blockLanes;
            int labelBase = block * blockLanes;
            Vector256<long> accLo = Vector256<long>.Zero;
            Vector256<long> accHi = Vector256<long>.Zero;

            for (int dim = 0; dim < Dims; dim++)
            {
                ref short blockRef = ref blocks[blockBase + dim * blockLanes];
                Vector128<short> packedValues = Unsafe.ReadUnaligned<Vector128<short>>(ref Unsafe.As<short, byte>(ref blockRef));
                Vector256<int> values = Avx2.ConvertToVector256Int32(packedValues);
                Vector256<int> diff = Avx2.Subtract(queryVectors[dim], values);
                Vector256<int> squared = Avx2.MultiplyLow(diff, diff);

                accLo = Avx2.Add(accLo, Avx2.ConvertToVector256Int64(squared.GetLower()));
                accHi = Avx2.Add(accHi, Avx2.ConvertToVector256Int64(squared.GetUpper()));
            }

            accLo.CopyTo(laneDistances);
            accHi.CopyTo(laneDistances[4..]);

            long worstDistance = candidateDistances[^1];
            for (int lane = 0; lane < blockLanes; lane++)
            {
                long distance = laneDistances[lane];
                if (distance > worstDistance)
                    continue;

                int id = ids[labelBase + lane];
                if (id < 0)
                    continue;

                InsertCandidateLong(candidateDistances, candidateIds, candidateLabels, distance, labels[labelBase + lane], id);
                worstDistance = candidateDistances[^1];
            }
        }
    }

    /// <summary>
    /// Scans IVF2 packed blocks with scalar int64 distances.
    /// </summary>
    /// <param name="candidateDistances">Mutable candidate int64 squared distances.</param>
    /// <param name="candidateIds">Mutable candidate original ids.</param>
    /// <param name="candidateLabels">Mutable candidate labels.</param>
    /// <param name="startBlock">Inclusive block offset.</param>
    /// <param name="endBlock">Exclusive block offset.</param>
    /// <param name="query">Int16 query vector.</param>
    private void ScanBlocksScalarLong(
        Span<long> candidateDistances,
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

                long distance = 0;
                for (int dim = 0; dim < Dims; dim++)
                {
                    int diff = query[dim] - blocks[blockBase + dim * blockLanes + lane];
                    distance += (long)diff * diff;
                    if (distance > candidateDistances[^1])
                        break;
                }

                InsertCandidateLong(candidateDistances, candidateIds, candidateLabels, distance, labels[labelBase + lane], id);
            }
        }
    }

    /// <summary>
    /// Inserts an IVF2 candidate into sorted candidate spans.
    /// </summary>
    /// <param name="distances">Sorted candidate distances.</param>
    /// <param name="ids">Sorted candidate ids.</param>
    /// <param name="labels">Sorted candidate labels.</param>
    /// <param name="distance">Candidate int64 squared distance.</param>
    /// <param name="label">Candidate label.</param>
    /// <param name="id">Candidate original id.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InsertCandidateLong(Span<long> distances, Span<int> ids, Span<byte> labels, long distance, byte label, int id)
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
    /// Checks whether an IVF2 cluster bounding box can still beat the current top-five bound.
    /// </summary>
    /// <param name="cluster">Cluster id.</param>
    /// <param name="query">Int16 query vector.</param>
    /// <param name="worstDistance">Current worst retained top-five distance.</param>
    /// <returns><see langword="true"/> when the bounding-box lower bound does not exceed <paramref name="worstDistance"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool BoundingBoxCanImproveLong(int cluster, ReadOnlySpan<short> query, long worstDistance)
    {
        long distance = 0;
        int bboxBase = cluster * Dims;
        for (int dim = 0; dim < Dims; dim++)
        {
            short value = query[dim];
            short min = bboxMinByCluster[bboxBase + dim];
            short max = bboxMaxByCluster[bboxBase + dim];
            if (value < min)
            {
                int diff = value - min;
                distance += (long)diff * diff;
                if (distance > worstDistance)
                    return false;
            }
            else if (value > max)
            {
                int diff = value - max;
                distance += (long)diff * diff;
                if (distance > worstDistance)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Finds nearest IVF2 quantized centroids for the query.
    /// </summary>
    /// <param name="query">Int16 query vector.</param>
    /// <param name="bestClusters">Mutable best cluster ids.</param>
    /// <param name="bestDistances">Mutable best centroid distances.</param>
    /// <param name="queryVectors">Pre-broadcast AVX2 query vectors, used only when AVX2 is available.</param>
    private void FindNearestCentroidsLong(
        ReadOnlySpan<short> query,
        scoped ReadOnlySpan<Vector256<int>> queryVectors,
        Span<int> bestClusters,
        Span<long> bestDistances)
    {
        if (Avx2.IsSupported)
        {
            FindNearestCentroidsAvx2Long(query, queryVectors, bestClusters, bestDistances);
            return;
        }

        for (int cluster = 0; cluster < clusters; cluster++)
            InsertProbeLong(bestClusters, bestDistances, cluster, CentroidDistanceLong(cluster, query));
    }

    /// <summary>
    /// Finds nearest IVF2 centroids eight clusters at a time with AVX2.
    /// </summary>
    /// <param name="query">Int16 query vector used for scalar tail clusters.</param>
    /// <param name="queryVectors">Pre-broadcast AVX2 query vectors.</param>
    /// <param name="bestClusters">Mutable best cluster ids.</param>
    /// <param name="bestDistances">Mutable best centroid distances.</param>
    private void FindNearestCentroidsAvx2Long(
        ReadOnlySpan<short> query,
        scoped ReadOnlySpan<Vector256<int>> queryVectors,
        Span<int> bestClusters,
        Span<long> bestDistances)
    {
        Span<long> laneDistances = stackalloc long[8];
        int cluster = 0;
        int simdLimit = clusters & ~7;
        for (; cluster < simdLimit; cluster += 8)
        {
            Vector256<long> accLo = Vector256<long>.Zero;
            Vector256<long> accHi = Vector256<long>.Zero;
            for (int dim = 0; dim < Dims; dim++)
            {
                ref short centroidRef = ref centroids[dim * clusters + cluster];
                Vector256<int> centroid = Avx2.ConvertToVector256Int32(Unsafe.ReadUnaligned<Vector128<short>>(ref Unsafe.As<short, byte>(ref centroidRef)));
                Vector256<int> diff = Avx2.Subtract(queryVectors[dim], centroid);
                Vector256<int> squared = Avx2.MultiplyLow(diff, diff);

                accLo = Avx2.Add(accLo, Avx2.ConvertToVector256Int64(squared.GetLower()));
                accHi = Avx2.Add(accHi, Avx2.ConvertToVector256Int64(squared.GetUpper()));
            }

            accLo.CopyTo(laneDistances);
            accHi.CopyTo(laneDistances[4..]);
            for (int lane = 0; lane < 8; lane++)
                InsertProbeLong(bestClusters, bestDistances, cluster + lane, laneDistances[lane]);
        }

        for (; cluster < clusters; cluster++)
            InsertProbeLong(bestClusters, bestDistances, cluster, CentroidDistanceLong(cluster, query));
    }

    /// <summary>
    /// Computes IVF2 quantized centroid distance for one cluster.
    /// </summary>
    /// <param name="cluster">Cluster id.</param>
    /// <param name="query">Int16 query vector.</param>
    /// <returns>Squared int64 distance to the centroid.</returns>
    private long CentroidDistanceLong(int cluster, ReadOnlySpan<short> query)
    {
        long distance = 0;
        for (int dim = 0; dim < Dims; dim++)
        {
            int diff = query[dim] - centroids[dim * clusters + cluster];
            distance += (long)diff * diff;
        }

        return distance;
    }

    /// <summary>
    /// Inserts an IVF2 centroid into the sorted nprobe list.
    /// </summary>
    /// <param name="clusters">Mutable best cluster ids.</param>
    /// <param name="distances">Mutable best centroid distances.</param>
    /// <param name="cluster">Candidate cluster id.</param>
    /// <param name="distance">Candidate centroid distance.</param>
    private static void InsertProbeLong(Span<int> clusters, Span<long> distances, int cluster, long distance)
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
