/// <summary>
/// Memory-mapped exact float32 reference vectors for IVF reranking.
/// </summary>
/// <remarks>
/// The file is optional and loaded only for <c>SCORER_MODE=ivf</c>. Mapping it
/// keeps the large exact corpus outside managed arrays; the hot path reads only
/// the handful of candidate rows produced by the int16 IVF stage.
/// </remarks>
internal sealed unsafe class IvfExactVectors : IDisposable
{
    private const int Magic = 0x31465845; // EXF1
    private const int Dims = 14;
    private const int HeaderBytes = sizeof(int) * 3;

    private readonly MemoryMappedFile mappedFile;
    private readonly MemoryMappedViewAccessor accessor;
    private readonly byte* basePointer;
    private readonly float* vectors;

    /// <summary>
    /// Creates a mapped exact-vector view from validated resources.
    /// </summary>
    /// <param name="mappedFile">Underlying memory-mapped file.</param>
    /// <param name="accessor">Read-only view accessor.</param>
    /// <param name="basePointer">Pointer acquired from the accessor.</param>
    /// <param name="count">Reference row count.</param>
    private IvfExactVectors(MemoryMappedFile mappedFile, MemoryMappedViewAccessor accessor, byte* basePointer, int count)
    {
        this.mappedFile = mappedFile;
        this.accessor = accessor;
        this.basePointer = basePointer;
        vectors = (float*)(basePointer + HeaderBytes);
        Count = count;
    }

    /// <summary>
    /// Gets the number of exact vectors in the mapped corpus.
    /// </summary>
    public int Count { get; }

    /// <summary>
    /// Attempts to memory-map an exact-vector file.
    /// </summary>
    /// <param name="path">Path to <c>references.exact.bin</c>.</param>
    /// <param name="expectedCount">Reference count expected by the IVF index.</param>
    /// <param name="exact">Loaded exact view when successful.</param>
    /// <param name="error">Human-readable load failure.</param>
    /// <returns><see langword="true"/> when the file exists and validates.</returns>
    public static bool TryLoad(string path, int expectedCount, out IvfExactVectors? exact, out string error)
    {
        exact = null;
        error = string.Empty;

        if (!File.Exists(path))
        {
            error = $"Exact rerank file not found: {path}";
            return false;
        }

        try
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                int magic = reader.ReadInt32();
                int count = reader.ReadInt32();
                int dims = reader.ReadInt32();
                long expectedBytes = HeaderBytes + checked((long)count * dims * sizeof(float));
                if (magic != Magic || count != expectedCount || dims != Dims || stream.Length != expectedBytes)
                {
                    error = "Invalid exact rerank header.";
                    return false;
                }
            }

            var mappedFile = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            var accessor = mappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            byte* pointer = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            exact = new IvfExactVectors(mappedFile, accessor, pointer, expectedCount);
            return true;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException or OverflowException)
        {
            error = $"Invalid exact rerank file: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Computes exact float32 squared L2 distance for one candidate row.
    /// </summary>
    /// <param name="query">Normalized query vector.</param>
    /// <param name="id">Original reference id.</param>
    /// <returns>Squared L2 distance in normalized float space.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Distance(ReadOnlySpan<float> query, int id)
    {
        float* row = vectors + (long)id * Dims;
        float sum = 0.0f;
        for (int dim = 0; dim < Dims; dim++)
        {
            float diff = query[dim] - row[dim];
            sum += diff * diff;
        }

        return sum;
    }

    /// <summary>
    /// Releases the acquired mmap pointer and file handles.
    /// </summary>
    public void Dispose()
    {
        accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        accessor.Dispose();
        mappedFile.Dispose();
    }
}
