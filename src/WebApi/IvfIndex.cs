/// <summary>
/// Runtime inverted-file vector index.
/// </summary>
/// <remarks>
/// The index is loaded only when <c>SCORER_MODE=ivf</c>. It scans the nearest
/// centroid cluster, optionally repairs boundary decisions with cluster bounding
/// boxes, and returns a fraud count compatible with the six prebuilt responses.
/// </remarks>
internal sealed class IvfIndex
{
    private const int Magic = 0x31465649; // IVF1
    private const int Dims = 14;

    private readonly int count;
    private readonly int clusters;
    private readonly int blockLanes;
    private readonly int totalBlocks;
    private readonly float[] centroids;
    private readonly short[] bboxMin;
    private readonly short[] bboxMax;
    private readonly int[] offsets;
    private readonly byte[] labels;
    private readonly int[] ids;
    private readonly short[] blocks;

    /// <summary>
    /// Creates an IVF index from already validated binary arrays.
    /// </summary>
    /// <param name="count">Reference row count.</param>
    /// <param name="clusters">Number of trained centroids.</param>
    /// <param name="blockLanes">Rows packed into each SIMD-friendly block.</param>
    /// <param name="totalBlocks">Total packed blocks.</param>
    /// <param name="centroids">Dimension-major centroid array.</param>
    /// <param name="bboxMin">Per-cluster int16 minimum bounds.</param>
    /// <param name="bboxMax">Per-cluster int16 maximum bounds.</param>
    /// <param name="offsets">Cluster block offsets.</param>
    /// <param name="labels">Padded one-byte labels.</param>
    /// <param name="ids">Padded original reference ids.</param>
    /// <param name="blocks">Dimension-major packed int16 vector blocks.</param>
    private IvfIndex(
        int count,
        int clusters,
        int blockLanes,
        int totalBlocks,
        float[] centroids,
        short[] bboxMin,
        short[] bboxMax,
        int[] offsets,
        byte[] labels,
        int[] ids,
        short[] blocks)
    {
        this.count = count;
        this.clusters = clusters;
        this.blockLanes = blockLanes;
        this.totalBlocks = totalBlocks;
        this.centroids = centroids;
        this.bboxMin = bboxMin;
        this.bboxMax = bboxMax;
        this.offsets = offsets;
        this.labels = labels;
        this.ids = ids;
        this.blocks = blocks;
    }

    /// <summary>
    /// Gets the number of reference rows represented by this index.
    /// </summary>
    public int Count => count;

    /// <summary>
    /// Attempts to load an IVF binary file.
    /// </summary>
    /// <param name="path">Path to <c>references.ivf.bin</c>.</param>
    /// <param name="index">Loaded index when the method succeeds.</param>
    /// <param name="error">Human-readable load failure when the method fails.</param>
    /// <returns><see langword="true"/> when the file exists and validates.</returns>
    public static bool TryLoad(string path, out IvfIndex? index, out string error)
    {
        index = null;
        error = string.Empty;

        if (!File.Exists(path))
        {
            error = $"IVF index not found: {path}";
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            int magic = reader.ReadInt32();
            if (magic != Magic)
            {
                error = "Invalid IVF index magic.";
                return false;
            }

            int count = reader.ReadInt32();
            int clusters = reader.ReadInt32();
            int dims = reader.ReadInt32();
            int scale = reader.ReadInt32();
            int blockLanes = reader.ReadInt32();
            int totalBlocks = reader.ReadInt32();
            if (count <= 0 || clusters <= 0 || dims != Dims || scale != 10000 || blockLanes <= 0 || totalBlocks <= 0)
            {
                error = "Invalid IVF index header.";
                return false;
            }

            int paddedRows = checked(totalBlocks * blockLanes);
            float[] centroids = new float[checked(clusters * Dims)];
            short[] bboxMin = new short[checked(clusters * Dims)];
            short[] bboxMax = new short[checked(clusters * Dims)];
            int[] offsets = new int[clusters + 1];
            byte[] labels = new byte[paddedRows];
            int[] ids = new int[paddedRows];
            short[] blocks = new short[checked(totalBlocks * Dims * blockLanes)];

            ReadArray(stream, centroids);
            ReadArray(stream, bboxMin);
            ReadArray(stream, bboxMax);
            ReadArray(stream, offsets);
            stream.ReadExactly(labels);
            ReadArray(stream, ids);
            ReadArray(stream, blocks);

            if (offsets[0] != 0 || offsets[^1] != totalBlocks)
            {
                error = "Invalid IVF offsets.";
                return false;
            }

            index = new IvfIndex(count, clusters, blockLanes, totalBlocks, centroids, bboxMin, bboxMax, offsets, labels, ids, blocks);
            return true;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException or OverflowException)
        {
            error = $"Invalid IVF index: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Finds the fraud count for one query through the configured IVF strategy.
    /// </summary>
    /// <param name="query">Normalized float query vector.</param>
    /// <param name="quantizedQuery">Int16 query vector using the same scale as the index.</param>
    /// <param name="options">Search and repair controls.</param>
    /// <returns>Fraud count from <c>0</c> through <c>5</c>.</returns>
    public byte FraudCount(ReadOnlySpan<float> query, ReadOnlySpan<short> quantizedQuery, IvfSearchOptions options)
    {
        int fastNProbe = Math.Clamp(options.FastNProbe, 1, clusters);
        bool fastRepair = options.BboxRepair && !options.BoundaryFull;
        byte frauds = FraudCountOnce(query, quantizedQuery, fastNProbe, fastRepair);

        if (options.BoundaryFull &&
            frauds >= options.RepairMinFrauds &&
            frauds <= options.RepairMaxFrauds)
        {
            int fullNProbe = Math.Clamp(Math.Max(options.FullNProbe, fastNProbe), 1, clusters);
            frauds = FraudCountOnce(query, quantizedQuery, fullNProbe, options.BboxRepair);
        }

        return frauds;
    }

    /// <summary>
    /// Runs one IVF pass with optional bounding-box repair.
    /// </summary>
    /// <param name="query">Normalized float query vector.</param>
    /// <param name="quantizedQuery">Int16 query vector.</param>
    /// <param name="nProbe">Number of nearest centroid clusters to scan.</param>
    /// <param name="repair">Whether bbox repair may scan additional clusters.</param>
    /// <returns>Fraud count from retained nearest candidates.</returns>
    private byte FraudCountOnce(ReadOnlySpan<float> query, ReadOnlySpan<short> quantizedQuery, int nProbe, bool repair)
    {
        Span<int> bestClusters = stackalloc int[nProbe];
        Span<float> bestDistances = stackalloc float[nProbe];
        bestDistances.Fill(float.PositiveInfinity);
        Span<long> candidateDistances = stackalloc long[5];
        Span<int> candidateIds = stackalloc int[5];
        Span<byte> candidateLabels = stackalloc byte[5];
        candidateDistances.Fill(long.MaxValue);
        candidateIds.Fill(int.MaxValue);

        for (int cluster = 0; cluster < clusters; cluster++)
        {
            float distance = 0.0f;
            for (int dim = 0; dim < Dims; dim++)
            {
                float diff = query[dim] - centroids[dim * clusters + cluster];
                distance += diff * diff;
            }

            InsertProbe(bestClusters, bestDistances, cluster, distance);
        }

        for (int i = 0; i < nProbe; i++)
        {
            int cluster = bestClusters[i];
            ScanBlocks(candidateDistances, candidateIds, candidateLabels, offsets[cluster], offsets[cluster + 1], quantizedQuery);
        }

        if (repair)
            RepairByBoundingBox(candidateDistances, candidateIds, candidateLabels, bestClusters, quantizedQuery);

        return FraudCount(candidateLabels);
    }

    /// <summary>
    /// Scans non-probed clusters whose bounding box could still contain a top-five vector.
    /// </summary>
    /// <param name="candidateDistances">Mutable candidate int16 distances.</param>
    /// <param name="candidateIds">Mutable candidate original ids.</param>
    /// <param name="candidateLabels">Mutable candidate labels.</param>
    /// <param name="probedClusters">Clusters already scanned by centroid distance.</param>
    /// <param name="query">Int16 query vector.</param>
    private void RepairByBoundingBox(
        Span<long> candidateDistances,
        Span<int> candidateIds,
        Span<byte> candidateLabels,
        scoped ReadOnlySpan<int> probedClusters,
        ReadOnlySpan<short> query)
    {
        int bitsetWords = (clusters + 63) >> 6;
        Span<ulong> scannedClusters = stackalloc ulong[bitsetWords];
        foreach (int cluster in probedClusters)
            scannedClusters[cluster >> 6] |= 1UL << (cluster & 63);

        long worstDistance = candidateDistances[^1];
        for (int cluster = 0; cluster < clusters; cluster++)
        {
            if (offsets[cluster] == offsets[cluster + 1] ||
                (scannedClusters[cluster >> 6] & (1UL << (cluster & 63))) != 0)
                continue;

            if (BoundingBoxLowerBound(cluster, query) <= worstDistance)
            {
                ScanBlocks(candidateDistances, candidateIds, candidateLabels, offsets[cluster], offsets[cluster + 1], query);
                worstDistance = candidateDistances[^1];
            }
        }
    }

    /// <summary>
    /// Scans packed blocks and inserts candidates that beat the current top-five bound.
    /// </summary>
    /// <param name="candidateDistances">Mutable candidate int16 distances.</param>
    /// <param name="candidateIds">Mutable candidate original ids.</param>
    /// <param name="candidateLabels">Mutable candidate labels.</param>
    /// <param name="startBlock">Inclusive block offset.</param>
    /// <param name="endBlock">Exclusive block offset.</param>
    /// <param name="query">Int16 query vector.</param>
    private void ScanBlocks(
        Span<long> candidateDistances,
        Span<int> candidateIds,
        Span<byte> candidateLabels,
        int startBlock,
        int endBlock,
        ReadOnlySpan<short> query)
    {
        if (Avx2.IsSupported && blockLanes == 8)
        {
            ScanBlocksAvx2(candidateDistances, candidateIds, candidateLabels, startBlock, endBlock, query);
            return;
        }

        ScanBlocksScalar(candidateDistances, candidateIds, candidateLabels, startBlock, endBlock, query);
    }

    /// <summary>
    /// Scans packed eight-lane blocks with AVX2 int32 squares and int64 accumulation.
    /// </summary>
    /// <param name="candidateDistances">Mutable candidate int16 distances.</param>
    /// <param name="candidateIds">Mutable candidate original ids.</param>
    /// <param name="candidateLabels">Mutable candidate labels.</param>
    /// <param name="startBlock">Inclusive block offset.</param>
    /// <param name="endBlock">Exclusive block offset.</param>
    /// <param name="query">Int16 query vector.</param>
    private void ScanBlocksAvx2(
        Span<long> candidateDistances,
        Span<int> candidateIds,
        Span<byte> candidateLabels,
        int startBlock,
        int endBlock,
        ReadOnlySpan<short> query)
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
                Vector256<int> queryValues = Vector256.Create((int)query[dim]);
                Vector256<int> diff = Avx2.Subtract(queryValues, values);
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

                InsertCandidate(candidateDistances, candidateIds, candidateLabels, distance, labels[labelBase + lane], id);
                worstDistance = candidateDistances[^1];
            }
        }
    }

    /// <summary>
    /// Scans packed blocks with scalar int64 distances for non-AVX2 runtimes.
    /// </summary>
    /// <param name="candidateDistances">Mutable candidate int16 distances.</param>
    /// <param name="candidateIds">Mutable candidate original ids.</param>
    /// <param name="candidateLabels">Mutable candidate labels.</param>
    /// <param name="startBlock">Inclusive block offset.</param>
    /// <param name="endBlock">Exclusive block offset.</param>
    /// <param name="query">Int16 query vector.</param>
    private void ScanBlocksScalar(
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
    /// <param name="distance">Candidate int16 squared distance.</param>
    /// <param name="label">Candidate label.</param>
    /// <param name="id">Candidate original id.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InsertCandidate(Span<long> distances, Span<int> ids, Span<byte> labels, long distance, byte label, int id)
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
    /// Computes the lower-bound distance from query to a cluster bounding box.
    /// </summary>
    /// <param name="cluster">Cluster id.</param>
    /// <param name="query">Int16 query vector.</param>
    /// <returns>Squared distance lower bound.</returns>
    private long BoundingBoxLowerBound(int cluster, ReadOnlySpan<short> query)
    {
        int bboxBase = cluster * Dims;
        long distance = 0;
        for (int dim = 0; dim < Dims; dim++)
        {
            short value = query[dim];
            if (value < bboxMin[bboxBase + dim])
            {
                int diff = value - bboxMin[bboxBase + dim];
                distance += (long)diff * diff;
            }
            else if (value > bboxMax[bboxBase + dim])
            {
                int diff = value - bboxMax[bboxBase + dim];
                distance += (long)diff * diff;
            }
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
    private static void InsertProbe(Span<int> clusters, Span<float> distances, int cluster, float distance)
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

    /// <summary>
    /// Reads a primitive array directly from the binary stream.
    /// </summary>
    /// <typeparam name="T">Unmanaged primitive type.</typeparam>
    /// <param name="stream">Input stream.</param>
    /// <param name="values">Destination array.</param>
    private static void ReadArray<T>(Stream stream, T[] values) where T : unmanaged =>
        stream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
}
