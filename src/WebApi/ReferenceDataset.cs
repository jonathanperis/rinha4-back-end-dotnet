using System.Runtime.InteropServices;

/// <summary>
/// Immutable layout metadata and bytes loaded from <c>references.bin</c>.
/// </summary>
/// <remarks>
/// The dataset stores all vectors first and labels second. Keeping those ranges
/// separate preserves the exact-search pointer layout while allowing the default
/// scorer to use an O(1) fine-bucket response lookup.
/// </remarks>
internal readonly struct ReferenceDataset
{
    private const int BinaryMagic = 0x35444852;
    private const int GroupCount = FraudVectorizer.FineGroupCount;

    /// <summary>Raw binary contents kept alive for unsafe pointer scans.</summary>
    public readonly byte[] Bytes;

    /// <summary>Number of reference vectors stored in the dataset.</summary>
    public readonly int Count;

    /// <summary>Logical vector dimension count before padding.</summary>
    public readonly int Dims;

    /// <summary>Physical vector dimension count stored in the binary file.</summary>
    public readonly int PaddedDims;

    /// <summary>Int16 quantization scale used by converter and API.</summary>
    public readonly int Scale;

    /// <summary>Whether the dataset contains fine-bucket offset metadata.</summary>
    public readonly bool HasGroupIndex;

    /// <summary>Byte offset where packed int16 vectors begin.</summary>
    public readonly int VectorsByteOffset;

    /// <summary>Byte offset where one-byte labels begin.</summary>
    public readonly int LabelsByteOffset;

    /// <summary>Precomputed fraud-score response index for each fine bucket.</summary>
    public readonly byte[] GroupResponseIndexes;

    /// <summary>
    /// Creates a dataset descriptor over already loaded binary bytes.
    /// </summary>
    /// <param name="bytes">Raw <c>references.bin</c> bytes.</param>
    /// <param name="count">Number of reference vectors.</param>
    /// <param name="dims">Logical vector dimension count.</param>
    /// <param name="paddedDims">Physical padded dimension count.</param>
    /// <param name="scale">Int16 quantization scale.</param>
    /// <param name="hasGroupIndex">Whether group offsets were present in the binary header.</param>
    /// <param name="vectorsByteOffset">Byte offset where packed vectors start.</param>
    /// <param name="labelsByteOffset">Byte offset where labels start.</param>
    /// <param name="groupResponseIndexes">Precomputed response index per group.</param>
    private ReferenceDataset(
        byte[] bytes,
        int count,
        int dims,
        int paddedDims,
        int scale,
        bool hasGroupIndex,
        int vectorsByteOffset,
        int labelsByteOffset,
        byte[] groupResponseIndexes)
    {
        Bytes = bytes;
        Count = count;
        Dims = dims;
        PaddedDims = paddedDims;
        Scale = scale;
        HasGroupIndex = hasGroupIndex;
        VectorsByteOffset = vectorsByteOffset;
        LabelsByteOffset = labelsByteOffset;
        GroupResponseIndexes = groupResponseIndexes;
    }

    /// <summary>
    /// Loads and validates <c>references.bin</c>, including optional fine-bucket indexes.
    /// </summary>
    /// <param name="dataPath">Path to the binary dataset.</param>
    /// <param name="exactSearch">When <see langword="true"/>, skips bucket-response precomputation.</param>
    /// <returns>A validated dataset descriptor ready for <see cref="FraudScorer"/>.</returns>
    public static ReferenceDataset Load(string dataPath, bool exactSearch)
    {
        if (!File.Exists(dataPath))
            ExitWithMessage($"Dataset not found: {dataPath}");

        Console.WriteLine("Loading dataset...");

        byte[] bytes = File.ReadAllBytes(dataPath);
        ReadOnlySpan<byte> span = bytes;
        int pos = 0;

        int first = ReadInt32(span, ref pos);
        bool hasGroupIndex = first == BinaryMagic;
        int count = hasGroupIndex ? ReadInt32(span, ref pos) : first;
        int dims = ReadInt32(span, ref pos);
        int paddedDims = ReadInt32(span, ref pos);
        int scale = ReadInt32(span, ref pos);
        int groupOffsetsByteOffset = pos;

        if (hasGroupIndex)
            pos += (GroupCount + 1) * sizeof(int);

        Console.WriteLine($"Dataset: {count:N0} vectors, {dims} dims (padded to {paddedDims}), scale {scale}, grouped {hasGroupIndex}");

        int vectorsByteOffset = pos;
        int labelsByteOffset = pos + count * paddedDims * sizeof(short);
        if (labelsByteOffset + count > bytes.Length)
            ExitWithMessage($"Invalid file size. Expected at least {labelsByteOffset + count}, got {bytes.Length}");

        byte[] groupResponseIndexes = hasGroupIndex && !exactSearch
            ? BuildGroupResponseIndexes(bytes, labelsByteOffset, groupOffsetsByteOffset, GroupCount)
            : Array.Empty<byte>();

        return new ReferenceDataset(
            bytes,
            count,
            dims,
            paddedDims,
            scale,
            hasGroupIndex,
            vectorsByteOffset,
            labelsByteOffset,
            groupResponseIndexes);
    }

    /// <summary>
    /// Builds one fraud-score response index per fine bucket using each bucket's label majority.
    /// </summary>
    /// <param name="bytes">Raw binary dataset bytes.</param>
    /// <param name="labelsByteOffset">Byte offset where the label array starts.</param>
    /// <param name="groupOffsetsByteOffset">Byte offset where fine-bucket offsets start.</param>
    /// <param name="groupCount">Number of fine buckets encoded in the dataset.</param>
    /// <returns>A byte array indexed by fine group and containing a 0..5 response index.</returns>
    private static byte[] BuildGroupResponseIndexes(byte[] bytes, int labelsByteOffset, int groupOffsetsByteOffset, int groupCount)
    {
        var indexes = GC.AllocateUninitializedArray<byte>(groupCount);

        for (int group = 0; group < groupCount; group++)
        {
            int start = ReadGroupOffset(bytes, groupOffsetsByteOffset, group);
            int end = ReadGroupOffset(bytes, groupOffsetsByteOffset, group + 1);
            int total = end - start;
            if (total == 0)
            {
                indexes[group] = 0;
                continue;
            }

            int frauds = 0;
            for (int i = start; i < end; i++)
                frauds += bytes[labelsByteOffset + i];

            indexes[group] = (byte)((frauds * 5 + total / 2) / total);
        }

        return indexes;
    }

    /// <summary>
    /// Reads the stored start offset for one fine-vector group.
    /// </summary>
    /// <param name="bytes">Binary dataset bytes.</param>
    /// <param name="groupOffsetsByteOffset">Byte offset where group offsets start.</param>
    /// <param name="group">Fine-bucket id whose offset should be read.</param>
    /// <returns>The vector index where the requested group starts.</returns>
    private static int ReadGroupOffset(byte[] bytes, int groupOffsetsByteOffset, int group) =>
        ReadInt32At(bytes, groupOffsetsByteOffset + group * sizeof(int));

    /// <summary>
    /// Reads a little-endian int32 from a byte span and advances the caller-owned cursor.
    /// </summary>
    /// <param name="span">Binary span being decoded.</param>
    /// <param name="pos">Current byte position; incremented by four on return.</param>
    /// <returns>The decoded int32 value.</returns>
    private static int ReadInt32(ReadOnlySpan<byte> span, ref int pos)
    {
        int value = MemoryMarshal.Read<int>(span.Slice(pos, sizeof(int)));
        pos += sizeof(int);
        return value;
    }

    /// <summary>
    /// Reads a little-endian int32 from an absolute byte-array offset.
    /// </summary>
    /// <param name="bytes">Binary dataset bytes.</param>
    /// <param name="pos">Absolute byte offset to read from.</param>
    /// <returns>The decoded int32 value.</returns>
    private static int ReadInt32At(byte[] bytes, int pos) => MemoryMarshal.Read<int>(bytes.AsSpan(pos, sizeof(int)));

    /// <summary>
    /// Prints a startup error and exits with the same one-line behavior as the previous top-level loader.
    /// </summary>
    /// <param name="message">Human-readable startup failure message.</param>
    private static void ExitWithMessage(string message)
    {
        Console.WriteLine(message);
        Environment.Exit(1);
    }
}
