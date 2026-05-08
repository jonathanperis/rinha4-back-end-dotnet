/// <summary>
/// Coarse bucket ANN index with profile fast-path and exact fallback for uncertain decisions.
/// </summary>
internal sealed unsafe class BucketIndex : IDisposable
{
    private const int Magic = 0x4b435542; // BUCK
    private const int Version = 1;
    private const int Dims = 14;
    private const int K = 5;
    private const int BucketCount = 4096;
    private const int ProfileCount = 1 << 22;
    private const int HeaderBytes = 7 * sizeof(int);
    private const byte LegitMask = 1;
    private const byte FraudMask = 2;

    private static readonly ushort[] NeighborKeyOrders = BuildNeighborKeyOrders();

    private readonly MemoryMappedFile mappedFile;
    private readonly MemoryMappedViewAccessor accessor;
    private byte* basePtr;
    private readonly int count;
    private readonly int scale;
    private readonly int* offsets;
    private readonly int* ids;
    private readonly byte* labels;
    private readonly short* vectors;
    private readonly ushort* profileCounts;
    private readonly byte* profileMasks;

    private BucketIndex(
        MemoryMappedFile mappedFile,
        MemoryMappedViewAccessor accessor,
        byte* basePtr,
        int count,
        int scale,
        int* offsets,
        int* ids,
        byte* labels,
        short* vectors,
        ushort* profileCounts,
        byte* profileMasks)
    {
        this.mappedFile = mappedFile;
        this.accessor = accessor;
        this.basePtr = basePtr;
        this.count = count;
        this.scale = scale;
        this.offsets = offsets;
        this.ids = ids;
        this.labels = labels;
        this.vectors = vectors;
        this.profileCounts = profileCounts;
        this.profileMasks = profileMasks;
    }

    public int Scale => scale;

    public static bool TryLoad(string path, out BucketIndex? index, out string error)
    {
        index = null;
        error = string.Empty;

        if (!File.Exists(path))
        {
            error = $"Bucket index not found: {path}";
            return false;
        }

        try
        {
            long length = new FileInfo(path).Length;
            var mappedFile = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            var accessor = mappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            byte* ptr = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);

            try
            {
                if (*(int*)ptr != Magic || *(int*)(ptr + 4) != Version)
                {
                    error = "Invalid bucket index header.";
                    return false;
                }

                int count = *(int*)(ptr + 8);
                int dims = *(int*)(ptr + 12);
                int scale = *(int*)(ptr + 16);
                int bucketCount = *(int*)(ptr + 20);
                int profileCount = *(int*)(ptr + 24);
                if (count <= 0 || dims != Dims || scale <= 0 || bucketCount != BucketCount || profileCount != ProfileCount)
                {
                    error = "Invalid bucket index shape.";
                    return false;
                }

                long offsetsOffset = HeaderBytes;
                long idsOffset = offsetsOffset + (BucketCount + 1L) * sizeof(int);
                long labelsOffset = idsOffset + count * sizeof(int);
                long vectorsOffset = labelsOffset + count;
                long profileCountsOffset = vectorsOffset + (long)count * Dims * sizeof(short);
                long profileMasksOffset = profileCountsOffset + ProfileCount * sizeof(ushort);
                long expectedLength = profileMasksOffset + ProfileCount;
                if (length != expectedLength)
                {
                    error = "Invalid bucket index length.";
                    return false;
                }

                int* offsets = (int*)(ptr + offsetsOffset);
                if (offsets[0] != 0 || offsets[BucketCount] != count)
                {
                    error = "Invalid bucket offsets.";
                    return false;
                }

                index = new BucketIndex(
                    mappedFile,
                    accessor,
                    ptr,
                    count,
                    scale,
                    offsets,
                    (int*)(ptr + idsOffset),
                    ptr + labelsOffset,
                    (short*)(ptr + vectorsOffset),
                    (ushort*)(ptr + profileCountsOffset),
                    ptr + profileMasksOffset);
                return true;
            }
            catch
            {
                accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                accessor.Dispose();
                mappedFile.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException or OverflowException)
        {
            error = $"Invalid bucket index: {ex.Message}";
            return false;
        }
    }

    public byte FraudCount(ReadOnlySpan<short> query, BucketSearchOptions options)
    {
        if (options.ProfileFastPath && TryProfileFastDecision(query, options, out byte fastFrauds))
            return fastFrauds;

        Span<long> topDist = stackalloc long[K];
        Span<int> topIds = stackalloc int[K];
        Span<byte> topLabels = stackalloc byte[K];
        topDist.Fill(long.MaxValue);
        topIds.Fill(int.MaxValue);

        int candidates = 0;
        ReadOnlySpan<ushort> neighborKeys = NeighborKeyOrders.AsSpan(BucketKey(query) * BucketCount, BucketCount);
        for (int neighborIndex = 0; neighborIndex < neighborKeys.Length; neighborIndex++)
        {
            int key = neighborKeys[neighborIndex];
            int start = offsets[key];
            int end = offsets[key + 1];
            for (int pos = start; pos < end; pos++)
            {
                Consider(pos, query, topDist, topIds, topLabels);
                candidates++;
                if (candidates >= options.MaxCandidates)
                    goto CandidateSearchDone;
            }

            if (candidates >= options.EarlyCandidates && StrongDecision(topLabels))
                goto CandidateSearchDone;
            if (candidates >= options.MinCandidates)
                goto CandidateSearchDone;
        }

    CandidateSearchDone:
        byte frauds = CountFrauds(topLabels);
        if (options.ExactFallback && (candidates < K || (frauds > 0 && frauds < K)))
            return ExactFraudCount(query);

        return frauds;
    }

    private bool TryProfileFastDecision(ReadOnlySpan<short> query, BucketSearchOptions options, out byte frauds)
    {
        frauds = 0;
        int key = ProfileKey(query);
        byte mask = profileMasks[key];
        if (mask == LegitMask && profileCounts[key] >= options.ProfileLegitMinCount)
            return true;
        if (mask == FraudMask && profileCounts[key] >= options.ProfileFraudMinCount)
        {
            frauds = K;
            return true;
        }

        return false;
    }

    private byte ExactFraudCount(ReadOnlySpan<short> query)
    {
        Span<long> topDist = stackalloc long[K];
        Span<int> topIds = stackalloc int[K];
        Span<byte> topLabels = stackalloc byte[K];
        topDist.Fill(long.MaxValue);
        topIds.Fill(int.MaxValue);

        for (int pos = 0; pos < count; pos++)
            Consider(pos, query, topDist, topIds, topLabels);

        return CountFrauds(topLabels);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Consider(int pos, ReadOnlySpan<short> query, Span<long> topDist, Span<int> topIds, Span<byte> topLabels)
    {
        int id = ids[pos];
        long dist = DistanceSquared(pos, query, topDist[^1]);
        if (dist > topDist[^1] || (dist == topDist[^1] && id >= topIds[^1]))
            return;

        int insertAt = K - 1;
        while (insertAt > 0 && (dist < topDist[insertAt - 1] || (dist == topDist[insertAt - 1] && id < topIds[insertAt - 1])))
        {
            topDist[insertAt] = topDist[insertAt - 1];
            topIds[insertAt] = topIds[insertAt - 1];
            topLabels[insertAt] = topLabels[insertAt - 1];
            insertAt--;
        }

        topDist[insertAt] = dist;
        topIds[insertAt] = id;
        topLabels[insertAt] = labels[pos];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long DistanceSquared(int pos, ReadOnlySpan<short> query, long cutoff)
    {
        short* vector = vectors + (long)pos * Dims;
        ref short q = ref MemoryMarshal.GetReference(query);
        long sum = 0;

        sum = AddDistance(sum, Unsafe.Add(ref q, 6), vector[6]); if (sum >= cutoff) return sum;
        sum = AddDistance(sum, Unsafe.Add(ref q, 10), vector[10]); if (sum >= cutoff) return sum;
        sum = AddDistance(sum, Unsafe.Add(ref q, 9), vector[9]); if (sum >= cutoff) return sum;
        sum = AddDistance(sum, Unsafe.Add(ref q, 5), vector[5]); if (sum >= cutoff) return sum;
        sum = AddDistance(sum, Unsafe.Add(ref q, 11), vector[11]); if (sum >= cutoff) return sum;
        sum = AddDistance(sum, Unsafe.Add(ref q, 2), vector[2]); if (sum >= cutoff) return sum;
        sum = AddDistance(sum, Unsafe.Add(ref q, 4), vector[4]); if (sum >= cutoff) return sum;
        sum = AddDistance(sum, Unsafe.Add(ref q, 7), vector[7]); if (sum >= cutoff) return sum;
        sum = AddDistance(sum, q, vector[0]); if (sum >= cutoff) return sum;
        sum = AddDistance(sum, Unsafe.Add(ref q, 1), vector[1]); if (sum >= cutoff) return sum;
        sum = AddDistance(sum, Unsafe.Add(ref q, 8), vector[8]); if (sum >= cutoff) return sum;
        sum = AddDistance(sum, Unsafe.Add(ref q, 12), vector[12]); if (sum >= cutoff) return sum;
        sum = AddDistance(sum, Unsafe.Add(ref q, 3), vector[3]); if (sum >= cutoff) return sum;
        return AddDistance(sum, Unsafe.Add(ref q, 13), vector[13]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long AddDistance(long sum, short left, short right)
    {
        long diff = (long)left - right;
        return sum + diff * diff;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte CountFrauds(ReadOnlySpan<byte> topLabels)
    {
        ref byte label = ref MemoryMarshal.GetReference(topLabels);
        return (byte)((label != 0 ? 1 : 0) +
                      (Unsafe.Add(ref label, 1) != 0 ? 1 : 0) +
                      (Unsafe.Add(ref label, 2) != 0 ? 1 : 0) +
                      (Unsafe.Add(ref label, 3) != 0 ? 1 : 0) +
                      (Unsafe.Add(ref label, 4) != 0 ? 1 : 0));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool StrongDecision(ReadOnlySpan<byte> topLabels)
    {
        byte frauds = CountFrauds(topLabels);
        return frauds == 0 || frauds == K;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int BucketKey(ReadOnlySpan<short> vector)
    {
        int amount = Bucket8(vector[0]);
        int ratio = Bucket8(vector[2]);
        int kmHome = Bucket8(vector[7]);
        int hour = Bucket4(vector[3]);
        int noLast = vector[5] < 0 ? 1 : 0;
        return amount | (ratio << 3) | (kmHome << 6) | (hour << 9) | (noLast << 11);
    }

    private int ProfileKey(ReadOnlySpan<short> vector)
    {
        int key = 0;
        key |= Bucket16(vector[2]);
        key |= Bucket8(vector[7]) << 4;
        key |= Bucket4(vector[8]) << 7;
        key |= Bucket4(vector[12]) << 9;
        key |= Bucket4(vector[0]) << 11;
        key |= (vector[5] < 0 ? 1 : 0) << 13;
        key |= (vector[9] > 0 ? 1 : 0) << 14;
        key |= (vector[10] > 0 ? 1 : 0) << 15;
        key |= (vector[11] > 0 ? 1 : 0) << 16;
        key |= Bucket4(vector[6]) << 17;
        key |= (vector[1] > scale / 10 ? 1 : 0) << 19;
        key |= Bucket4(vector[13]) << 20;
        return key;
    }

    private int Bucket4(short value) => value <= 0 ? 0 : Math.Clamp(value * 4 / (scale + 1), 0, 3);

    private int Bucket8(short value) => value <= 0 ? 0 : Math.Clamp(value * 8 / (scale + 1), 0, 7);

    private int Bucket16(short value) => value <= 0 ? 0 : Math.Clamp(value * 16 / (scale + 1), 0, 15);

    private static ushort[] BuildNeighborKeyOrders()
    {
        var orders = new ushort[BucketCount * BucketCount];
        var buffer = new ushort[BucketCount];
        var seen = new byte[BucketCount];

        for (int bucketKey = 0; bucketKey < BucketCount; bucketKey++)
        {
            int amount = bucketKey & 7;
            int ratio = (bucketKey >> 3) & 7;
            int kmHome = (bucketKey >> 6) & 7;
            int hour = (bucketKey >> 9) & 3;
            int noLast = (bucketKey >> 11) & 1;
            int count = FillNeighborKeys(amount, ratio, kmHome, hour, noLast, buffer, seen);
            if (count != BucketCount)
                throw new InvalidOperationException($"neighbor key table incomplete for bucket {bucketKey}, count={count}");

            buffer.CopyTo(orders.AsSpan(bucketKey * BucketCount, BucketCount));
        }

        return orders;
    }

    private static int FillNeighborKeys(int amount, int ratio, int kmHome, int hour, int noLast, Span<ushort> output, Span<byte> seen)
    {
        int count = 0;
        seen.Clear();
        for (int radius = 0; radius < 8; radius++)
        {
            for (int a = Math.Max(amount - radius, 0); a <= Math.Min(amount + radius, 7); a++)
            for (int r = Math.Max(ratio - radius, 0); r <= Math.Min(ratio + radius, 7); r++)
            for (int k = Math.Max(kmHome - radius, 0); k <= Math.Min(kmHome + radius, 7); k++)
            for (int h = Math.Max(hour - radius, 0); h <= Math.Min(hour + radius, 3); h++)
            {
                int lastStart = radius >= 2 ? 0 : noLast;
                int lastEnd = radius >= 2 ? 1 : noLast;
                for (int last = lastStart; last <= lastEnd; last++)
                {
                    int key = a | (r << 3) | (k << 6) | (h << 9) | (last << 11);
                    if (seen[key] != 0)
                        continue;

                    seen[key] = 1;
                    output[count++] = (ushort)key;
                }
            }
        }

        return count;
    }

    public void Dispose()
    {
        if (basePtr != null)
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            basePtr = null;
        }

        accessor.Dispose();
        mappedFile.Dispose();
    }
}
