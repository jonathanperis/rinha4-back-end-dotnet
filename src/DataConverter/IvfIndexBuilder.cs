/// <summary>
/// Build-time configuration for the IVF reference index.
/// </summary>
/// <param name="Clusters">Number of inverted-file clusters to train.</param>
/// <param name="TrainSample">Number of evenly spaced references used by k-means.</param>
/// <param name="Iterations">Number of k-means refinement iterations.</param>
/// <param name="Scale">Quantization scale used for int16 persisted vectors.</param>
internal readonly record struct IvfBuildOptions(int Clusters, int TrainSample, int Iterations, int Scale);

/// <summary>
/// Builds the <c>references.ivf.bin</c> file used by the WebApi IVF scorer mode.
/// </summary>
/// <remarks>
/// The layout mirrors the high-ranking C++ IVF idea: compact int16 vector blocks,
/// one-byte labels, centroids, and per-cluster bounding boxes. It is intentionally
/// used directly by the runtime scorer.
/// </remarks>
internal static class IvfIndexBuilder
{
    private const int MagicV2 = 0x32465649; // IVF2
    private const int Dims = 14;
    private const int BlockLanes = 8;
    public const int DefaultScale = 10000;

    /// <summary>
    /// Trains, packs, and writes an IVF binary index.
    /// </summary>
    /// <param name="outputPath">Destination <c>references.ivf.bin</c> path.</param>
    /// <param name="vectors">Row-major normalized vectors, with <c>14</c> floats per row.</param>
    /// <param name="labels">One-byte labels aligned with <paramref name="vectors"/> rows.</param>
    /// <param name="count">Number of reference rows.</param>
    /// <param name="options">K-means and cluster-count options.</param>
    public static void Write(string outputPath, float[] vectors, byte[] labels, int count, IvfBuildOptions options)
    {
        int clusters = Math.Min(Math.Min(options.Clusters, count), ushort.MaxValue);
        int scale = Math.Clamp(options.Scale, 1, short.MaxValue);
        Console.WriteLine("Training IVF centroids...");
        float[] centroids = TrainCentroids(vectors, count, clusters, options.TrainSample, options.Iterations);

        Console.WriteLine("Assigning IVF clusters...");
        var assignments = GC.AllocateUninitializedArray<ushort>(count);
        var rowCounts = new int[clusters];
        AssignClusters(vectors, count, centroids, clusters, assignments, rowCounts);

        var offsets = new int[clusters + 1];
        for (int cluster = 0; cluster < clusters; cluster++)
            offsets[cluster + 1] = offsets[cluster] + ((rowCounts[cluster] + BlockLanes - 1) / BlockLanes);

        int totalBlocks = offsets[clusters];
        int paddedRows = totalBlocks * BlockLanes;
        var labelsOut = new byte[paddedRows];
        var idsOut = GC.AllocateUninitializedArray<int>(paddedRows);
        Array.Fill(idsOut, -1);
        var blocks = GC.AllocateUninitializedArray<short>(totalBlocks * Dims * BlockLanes);
        Array.Fill(blocks, short.MaxValue);
        var bboxMin = new short[clusters * Dims];
        var bboxMax = new short[clusters * Dims];
        Array.Fill(bboxMin, short.MaxValue);
        Array.Fill(bboxMax, short.MinValue);

        var positions = new int[clusters];
        Console.WriteLine("Packing IVF blocks...");
        for (int row = 0; row < count; row++)
        {
            int cluster = assignments[row];
            int position = positions[cluster]++;
            int block = offsets[cluster] + (position / BlockLanes);
            int lane = position % BlockLanes;
            int rowBase = row * Dims;
            int blockBase = block * Dims * BlockLanes;
            int labelBase = block * BlockLanes;

            labelsOut[labelBase + lane] = labels[row];
            idsOut[labelBase + lane] = row;

            for (int dim = 0; dim < Dims; dim++)
            {
                short value = Quantize(vectors[rowBase + dim], scale);
                blocks[blockBase + dim * BlockLanes + lane] = value;
                int bboxIndex = cluster * Dims + dim;
                if (value < bboxMin[bboxIndex]) bboxMin[bboxIndex] = value;
                if (value > bboxMax[bboxIndex]) bboxMax[bboxIndex] = value;
            }
        }

        for (int cluster = 0; cluster < clusters; cluster++)
        {
            if (rowCounts[cluster] != 0)
                continue;

            int bboxBase = cluster * Dims;
            for (int dim = 0; dim < Dims; dim++)
            {
                bboxMin[bboxBase + dim] = 0;
                bboxMax[bboxBase + dim] = 0;
            }
        }

        short[] transposedCentroids = QuantizeTransposeCentroids(centroids, clusters, scale);
        short[] transposedBboxMin = TransposeBounds(bboxMin, clusters);
        short[] transposedBboxMax = TransposeBounds(bboxMax, clusters);
        using var stream = File.Create(outputPath);
        using var writer = new BinaryWriter(stream);

        writer.Write(MagicV2);
        writer.Write(count);
        writer.Write(clusters);
        writer.Write(Dims);
        writer.Write(scale);
        writer.Write(BlockLanes);
        writer.Write(totalBlocks);
        WriteShorts(writer, transposedCentroids);
        WriteShorts(writer, transposedBboxMin);
        WriteShorts(writer, transposedBboxMax);
        WriteInts(writer, offsets);
        writer.Write(labelsOut);
        WriteInts(writer, idsOut);
        WriteShorts(writer, blocks);
        writer.Flush();
    }

    /// <summary>
    /// Trains deterministic k-means centroids from evenly spaced sample rows.
    /// </summary>
    /// <param name="vectors">Row-major normalized reference vectors.</param>
    /// <param name="count">Reference row count.</param>
    /// <param name="clusters">Number of centroids to train.</param>
    /// <param name="trainSample">Requested training sample size.</param>
    /// <param name="iterations">K-means iteration count.</param>
    /// <returns>Row-major centroid array.</returns>
    private static float[] TrainCentroids(float[] vectors, int count, int clusters, int trainSample, int iterations)
    {
        int sample = Math.Max(clusters, Math.Min(trainSample, count));
        var sampleRows = GC.AllocateUninitializedArray<int>(sample);
        for (int i = 0; i < sample; i++)
            sampleRows[i] = (int)((long)i * count / sample);

        var centroids = new float[clusters * Dims];
        for (int cluster = 0; cluster < clusters; cluster++)
        {
            int sampleIndex = (int)((long)cluster * sample / clusters);
            Array.Copy(vectors, sampleRows[sampleIndex] * Dims, centroids, cluster * Dims, Dims);
        }

        var sums = new double[clusters * Dims];
        var counts = new int[clusters];
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            Array.Clear(sums);
            Array.Clear(counts);

            foreach (int row in sampleRows)
            {
                int cluster = NearestCentroid(vectors, row * Dims, centroids, clusters);
                counts[cluster]++;
                int vectorBase = row * Dims;
                int sumBase = cluster * Dims;
                for (int dim = 0; dim < Dims; dim++)
                    sums[sumBase + dim] += vectors[vectorBase + dim];
            }

            for (int cluster = 0; cluster < clusters; cluster++)
            {
                int clusterCount = counts[cluster];
                if (clusterCount == 0)
                    continue;

                double inv = 1.0 / clusterCount;
                int centroidBase = cluster * Dims;
                for (int dim = 0; dim < Dims; dim++)
                    centroids[centroidBase + dim] = (float)(sums[centroidBase + dim] * inv);
            }
        }

        return centroids;
    }

    /// <summary>
    /// Finds the closest centroid for one row using squared Euclidean distance.
    /// </summary>
    /// <param name="vectors">Row-major vector array.</param>
    /// <param name="vectorBase">Offset of the row to compare.</param>
    /// <param name="centroids">Row-major centroid array.</param>
    /// <param name="clusters">Number of centroids.</param>
    /// <returns>Closest centroid index.</returns>
    private static int NearestCentroid(float[] vectors, int vectorBase, float[] centroids, int clusters)
    {
        int best = 0;
        float bestDistance = float.PositiveInfinity;
        for (int cluster = 0; cluster < clusters; cluster++)
        {
            int centroidBase = cluster * Dims;
            float distance = 0.0f;
            for (int dim = 0; dim < Dims; dim++)
            {
                float diff = vectors[vectorBase + dim] - centroids[centroidBase + dim];
                distance += diff * diff;
            }

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = cluster;
            }
        }

        return best;
    }

    /// <summary>
    /// Assigns every reference row to the nearest centroid and accumulates per-cluster row counts.
    /// </summary>
    /// <param name="vectors">Row-major normalized reference vectors.</param>
    /// <param name="count">Reference row count.</param>
    /// <param name="centroids">Row-major centroid array.</param>
    /// <param name="clusters">Number of trained centroids.</param>
    /// <param name="assignments">Destination row-to-cluster mapping.</param>
    /// <param name="rowCounts">Destination cluster sizes.</param>
    /// <remarks>
    /// This is build-time work, but 4096 clusters make the scalar single-threaded pass expensive.
    /// Fixed worker ranges keep writes deterministic while using available build CPUs.
    /// </remarks>
    private static void AssignClusters(
        float[] vectors,
        int count,
        float[] centroids,
        int clusters,
        ushort[] assignments,
        int[] rowCounts)
    {
        int workers = Math.Max(1, Environment.ProcessorCount);
        var partialCounts = new int[workers][];

        Parallel.For(0, workers, worker =>
        {
            int start = (int)((long)worker * count / workers);
            int end = (int)((long)(worker + 1) * count / workers);
            int[] localCounts = new int[clusters];

            for (int row = start; row < end; row++)
            {
                int cluster = NearestCentroid(vectors, row * Dims, centroids, clusters);
                assignments[row] = (ushort)cluster;
                localCounts[cluster]++;
            }

            partialCounts[worker] = localCounts;
        });

        for (int worker = 0; worker < workers; worker++)
        {
            int[] localCounts = partialCounts[worker];
            for (int cluster = 0; cluster < clusters; cluster++)
                rowCounts[cluster] += localCounts[cluster];
        }
    }

    /// <summary>
    /// Quantizes row-major centroids into dimension-major layout for query-time scanning.
    /// </summary>
    /// <param name="centroids">Row-major centroid array.</param>
    /// <param name="clusters">Number of centroids.</param>
    /// <returns>Dimension-major quantized centroid array.</returns>
    private static short[] QuantizeTransposeCentroids(float[] centroids, int clusters, int scale)
    {
        var transposed = new short[centroids.Length];
        for (int cluster = 0; cluster < clusters; cluster++)
        {
            int centroidBase = cluster * Dims;
            for (int dim = 0; dim < Dims; dim++)
                transposed[dim * clusters + cluster] = Quantize(centroids[centroidBase + dim], scale);
        }

        return transposed;
    }

    /// <summary>
    /// Converts cluster-major bounding boxes into dimension-major layout.
    /// </summary>
    /// <param name="bounds">Cluster-major int16 bounds.</param>
    /// <param name="clusters">Number of clusters.</param>
    /// <returns>Dimension-major int16 bounds.</returns>
    private static short[] TransposeBounds(short[] bounds, int clusters)
    {
        var transposed = new short[bounds.Length];
        for (int cluster = 0; cluster < clusters; cluster++)
        {
            int clusterBase = cluster * Dims;
            for (int dim = 0; dim < Dims; dim++)
                transposed[dim * clusters + cluster] = bounds[clusterBase + dim];
        }

        return transposed;
    }

    /// <summary>
    /// Quantizes a normalized float to int16 using the configured IVF scale.
    /// </summary>
    /// <param name="value">Normalized vector value, including the allowed <c>-1</c> sentinel.</param>
    /// <returns>Scaled int16 value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short Quantize(float value, int scale)
    {
        if (value < -1.0f) value = -1.0f;
        if (value > 1.0f) value = 1.0f;
        return (short)MathF.Round(value * scale);
    }

    /// <summary>
    /// Writes int values without per-element boxing.
    /// </summary>
    /// <param name="writer">Binary writer positioned at the destination range.</param>
    /// <param name="values">Values to write.</param>
    private static void WriteInts(BinaryWriter writer, int[] values)
    {
        foreach (int value in values)
            writer.Write(value);
    }

    /// <summary>
    /// Writes int16 values without per-element boxing.
    /// </summary>
    /// <param name="writer">Binary writer positioned at the destination range.</param>
    /// <param name="values">Values to write.</param>
    private static void WriteShorts(BinaryWriter writer, short[] values)
    {
        foreach (short value in values)
            writer.Write(value);
    }
}
