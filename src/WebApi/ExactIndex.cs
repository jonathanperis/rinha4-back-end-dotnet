/// <summary>
/// Flat exact KNN index: quantized 16-short rows plus one label byte per row.
/// </summary>
internal sealed class ExactIndex
{
    private const int Stride = 16;
    private const int K = 5;
    public const int DefaultScale = 8192;

    private readonly int count;
    private readonly short[] references;
    private readonly byte[] labels;

    private ExactIndex(int count, short[] references, byte[] labels)
    {
        this.count = count;
        this.references = references;
        this.labels = labels;
    }

    public int Scale => DefaultScale;

    public static bool TryLoad(string path, out ExactIndex? index, out string error)
    {
        index = null;
        error = string.Empty;

        if (!File.Exists(path))
        {
            error = $"Exact index not found: {path}";
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            int count = reader.ReadInt32();
            if (count <= 0)
            {
                error = "Invalid exact index header.";
                return false;
            }

            long expectedLength = sizeof(int) + (long)count * Stride * sizeof(short) + count;
            if (stream.Length != expectedLength)
            {
                error = "Invalid exact index length.";
                return false;
            }

            short[] references = GC.AllocateUninitializedArray<short>(checked(count * Stride), pinned: true);
            stream.ReadExactly(MemoryMarshal.AsBytes(references.AsSpan()));
            byte[] labels = GC.AllocateUninitializedArray<byte>(count);
            stream.ReadExactly(labels);

            index = new ExactIndex(count, references, labels);
            return true;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException or OverflowException)
        {
            error = $"Invalid exact index: {ex.Message}";
            return false;
        }
    }

    public byte FraudCount(ReadOnlySpan<short> query)
    {
        Span<uint> topDistances = stackalloc uint[K];
        Span<byte> topLabels = stackalloc byte[K];
        topDistances.Fill(uint.MaxValue);

        if (Avx2.IsSupported)
            ScanAvx2(query, topDistances, topLabels);
        else
            ScanScalar(query, topDistances, topLabels);

        return (byte)(topLabels[0] + topLabels[1] + topLabels[2] + topLabels[3] + topLabels[4]);
    }

    private void ScanAvx2(ReadOnlySpan<short> query, Span<uint> topDistances, Span<byte> topLabels)
    {
        Vector256<short> q = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(query));
        ref short refsBase = ref MemoryMarshal.GetArrayDataReference(references);
        uint bound = uint.MaxValue;

        for (int row = 0; row < count; row++)
        {
            Vector256<short> reference = Vector256.LoadUnsafe(ref refsBase, (nuint)(row * Stride));
            Vector256<short> diff = Avx2.Subtract(q, reference);
            (Vector256<int> lo, Vector256<int> hi) = Vector256.Widen(diff);
            Vector256<int> sum = Avx2.Add(Avx2.MultiplyLow(lo, lo), Avx2.MultiplyLow(hi, hi));
            uint distance = (uint)Vector256.Sum(sum);

            if (distance >= bound)
                continue;

            InsertTopK(topDistances, topLabels, distance, labels[row]);
            bound = topDistances[K - 1];
        }
    }

    private void ScanScalar(ReadOnlySpan<short> query, Span<uint> topDistances, Span<byte> topLabels)
    {
        uint bound = uint.MaxValue;
        for (int row = 0; row < count; row++)
        {
            int rowBase = row * Stride;
            uint distance = 0;
            for (int dim = 0; dim < Stride; dim++)
            {
                int diff = query[dim] - references[rowBase + dim];
                distance += (uint)(diff * diff);
                if (distance >= bound)
                    goto Next;
            }

            InsertTopK(topDistances, topLabels, distance, labels[row]);
            bound = topDistances[K - 1];
        Next:;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InsertTopK(Span<uint> distances, Span<byte> topLabels, uint distance, byte label)
    {
        int pos = K - 1;
        while (pos > 0 && distances[pos - 1] > distance)
        {
            distances[pos] = distances[pos - 1];
            topLabels[pos] = topLabels[pos - 1];
            pos--;
        }

        distances[pos] = distance;
        topLabels[pos] = label;
    }
}
