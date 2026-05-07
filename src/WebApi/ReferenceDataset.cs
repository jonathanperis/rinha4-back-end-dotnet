/// <summary>
/// Runtime dataset layout metadata and fine-bucket response table.
/// </summary>
/// <remarks>
/// Startup reads <c>references.bin</c> and loads one response index per fine bucket.
/// </remarks>
internal readonly struct ReferenceDataset
{
    private const int BinaryMagic = 0x37444852;
    private const int GroupCount = FraudVectorizer.FineGroupCount;

    /// <summary>Logical vector dimension count before padding.</summary>
    public readonly int Dims;

    /// <summary>Physical request-vector stride kept in the binary metadata.</summary>
    public readonly int PaddedDims;

    /// <summary>Int16 quantization scale used by converter and API.</summary>
    public readonly int Scale;

    /// <summary>Fraud-score response index for each fine bucket.</summary>
    public readonly byte[] GroupResponseIndexes;

    /// <summary>
    /// Creates a dataset descriptor with only runtime-required metadata.
    /// </summary>
    /// <param name="dims">Logical vector dimension count.</param>
    /// <param name="paddedDims">Physical padded dimension count.</param>
    /// <param name="scale">Int16 quantization scale.</param>
    /// <param name="groupResponseIndexes">Precomputed response index per group.</param>
    private ReferenceDataset(int dims, int paddedDims, int scale, byte[] groupResponseIndexes)
    {
        Dims = dims;
        PaddedDims = paddedDims;
        Scale = scale;
        GroupResponseIndexes = groupResponseIndexes;
    }

    /// <summary>
    /// Loads and validates <c>references.bin</c>, then reads bucket responses.
    /// </summary>
    /// <param name="dataPath">Path to the binary dataset.</param>
    /// <returns>A validated dataset descriptor ready for <see cref="FraudScorer"/>.</returns>
    public static ReferenceDataset Load(string dataPath)
    {
        if (!File.Exists(dataPath))
            ExitWithMessage($"Dataset not found: {dataPath}");

        Console.WriteLine("Loading dataset...");

        byte[] bytes = File.ReadAllBytes(dataPath);
        ReadOnlySpan<byte> span = bytes;
        int pos = 0;

        int magic = ReadInt32(span, ref pos);
        if (magic != BinaryMagic)
            ExitWithMessage("Invalid dataset format: missing grouped binary header.");

        int count = ReadInt32(span, ref pos);
        int dims = ReadInt32(span, ref pos);
        int paddedDims = ReadInt32(span, ref pos);
        int scale = ReadInt32(span, ref pos);

        Console.WriteLine($"Dataset: {count:N0} vectors, {dims} dims (padded to {paddedDims}), scale {scale}");

        if (pos + GroupCount > bytes.Length)
            ExitWithMessage($"Invalid file size. Expected at least {pos + GroupCount}, got {bytes.Length}");

        byte[] groupResponseIndexes = bytes.AsSpan(pos, GroupCount).ToArray();
        return new ReferenceDataset(dims, paddedDims, scale, groupResponseIndexes);
    }

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
    /// Prints a startup error and exits with the same one-line behavior as the previous top-level loader.
    /// </summary>
    /// <param name="message">Human-readable startup failure message.</param>
    private static void ExitWithMessage(string message)
    {
        Console.WriteLine(message);
        Environment.Exit(1);
    }
}
