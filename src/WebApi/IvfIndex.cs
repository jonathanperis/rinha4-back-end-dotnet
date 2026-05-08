/// <summary>
/// Runtime inverted-file vector index.
/// </summary>
/// <remarks>
/// The index is loaded at startup. It scans the nearest centroid cluster,
/// optionally repairs boundary decisions with cluster bounding boxes, and
/// returns a fraud count compatible with the six prebuilt responses.
/// </remarks>
internal sealed partial class IvfIndex
{
    private const int MagicV2 = 0x32465649; // IVF2
    private const int Dims = 14;

    private readonly int clusters;
    private readonly int scale;
    private readonly int blockLanes;
    private readonly short[] centroids;
    private readonly short[] bboxMin;
    private readonly short[] bboxMax;
    private readonly int[] offsets;
    private readonly byte[] labels;
    private readonly int[] ids;
    private readonly short[] blocks;

    /// <summary>
    /// Creates an IVF index from already validated binary arrays.
    /// </summary>
    /// <param name="clusters">Number of trained centroids.</param>
    /// <param name="scale">Quantization scale stored in the IVF header.</param>
    /// <param name="blockLanes">Rows packed into each SIMD-friendly block.</param>
    /// <param name="centroids">Dimension-major int16 centroid array.</param>
    /// <param name="bboxMin">Per-cluster int16 minimum bounds.</param>
    /// <param name="bboxMax">Per-cluster int16 maximum bounds.</param>
    /// <param name="offsets">Cluster block offsets.</param>
    /// <param name="labels">Padded one-byte labels.</param>
    /// <param name="ids">Padded original reference ids.</param>
    /// <param name="blocks">Dimension-major packed int16 vector blocks.</param>
    private IvfIndex(
        int clusters,
        int scale,
        int blockLanes,
        short[] centroids,
        short[] bboxMin,
        short[] bboxMax,
        int[] offsets,
        byte[] labels,
        int[] ids,
        short[] blocks)
    {
        this.clusters = clusters;
        this.scale = scale;
        this.blockLanes = blockLanes;
        this.centroids = centroids;
        this.bboxMin = bboxMin;
        this.bboxMax = bboxMax;
        this.offsets = offsets;
        this.labels = labels;
        this.ids = ids;
        this.blocks = blocks;
    }

    /// <summary>
    /// Gets the quantization scale stored in the IVF file header.
    /// </summary>
    public int Scale => scale;

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
            if (magic != MagicV2)
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
            if (count <= 0 ||
                clusters <= 0 ||
                dims != Dims ||
                scale <= 0 ||
                scale > short.MaxValue ||
                blockLanes <= 0 ||
                totalBlocks <= 0)
            {
                error = "Invalid IVF index header.";
                return false;
            }

            int paddedRows = checked(totalBlocks * blockLanes);
            short[] centroids = new short[checked(clusters * Dims)];
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

            index = new IvfIndex(clusters, scale, blockLanes, centroids, bboxMin, bboxMax, offsets, labels, ids, blocks);
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
    /// <param name="quantizedQuery">Int16 query vector using the same scale as the index.</param>
    /// <param name="options">Search and repair controls.</param>
    /// <returns>Fraud count from <c>0</c> through <c>5</c>.</returns>
    public byte FraudCount(ReadOnlySpan<short> quantizedQuery, IvfSearchOptions options)
    {
        int fastNProbe = Math.Clamp(options.FastNProbe, 1, clusters);
        bool repairsAllCounts = options.BoundaryFull && options.RepairMinFrauds == 0 && options.RepairMaxFrauds == 5;
        bool fastRepair = options.BboxRepair && (!options.BoundaryFull || repairsAllCounts);
        byte frauds = FraudCountOnceLong(quantizedQuery, fastNProbe, fastRepair);

        if (options.BoundaryFull &&
            !repairsAllCounts &&
            frauds >= options.RepairMinFrauds &&
            frauds <= options.RepairMaxFrauds)
        {
            int fullNProbe = Math.Clamp(Math.Max(options.FullNProbe, fastNProbe), 1, clusters);
            frauds = FraudCountOnceLong(quantizedQuery, fullNProbe, options.BboxRepair);
        }

        return frauds;
    }

    /// <summary>
    /// Reads a primitive array directly from the binary stream.
    /// </summary>
    /// <typeparam name="T">Unmanaged primitive type.</typeparam>
    /// <param name="stream">Input stream.</param>
    /// <param name="values">Destination array.</param>
    private static void ReadArray<T>(Stream stream, T[] values) where T : unmanaged =>
        stream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));

    /// <summary>
    /// Broadcasts one quantized query into per-dimension AVX2 vectors.
    /// </summary>
    /// <param name="query">Int16 query vector.</param>
    /// <param name="queryVectors">Destination vector span with one broadcast vector per dimension.</param>
    private static void FillQueryVectors(ReadOnlySpan<short> query, Span<Vector256<int>> queryVectors)
    {
        for (int dim = 0; dim < Dims; dim++)
            queryVectors[dim] = Vector256.Create((int)query[dim]);
    }
}
