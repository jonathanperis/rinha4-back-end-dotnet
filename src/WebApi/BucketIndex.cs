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
    private const int RiskyFineExtraBits = 3;
    private const int RiskyFineBucketCount = BucketCount << RiskyFineExtraBits;
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
    private readonly int[] riskyPositions;
    private readonly int[] riskyFineBucketOffsets;
    private readonly int[] riskyCoarseFineOffsets;
    private readonly int[] riskyFineKeys;

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
        byte* profileMasks,
        int[] riskyPositions,
        int[] riskyFineBucketOffsets,
        int[] riskyCoarseFineOffsets,
        int[] riskyFineKeys)
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
        this.riskyPositions = riskyPositions;
        this.riskyFineBucketOffsets = riskyFineBucketOffsets;
        this.riskyCoarseFineOffsets = riskyCoarseFineOffsets;
        this.riskyFineKeys = riskyFineKeys;
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

                bool buildRiskyFallback = IsRiskyFallbackEnabled();
                BuildRiskyFallbackIndex(
                    buildRiskyFallback,
                    (short*)(ptr + vectorsOffset),
                    count,
                    scale,
                    out int[] riskyPositions,
                    out int[] riskyFineBucketOffsets,
                    out int[] riskyCoarseFineOffsets,
                    out int[] riskyFineKeys);

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
                    ptr + profileMasksOffset,
                    riskyPositions,
                    riskyFineBucketOffsets,
                    riskyCoarseFineOffsets,
                    riskyFineKeys);
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
        if (candidates < K)
            return ExactFraudCount(query);

        if (ShouldUseExactFallback(query, frauds, options))
            return options.RiskyFallback ? RiskyFraudCount(query, allowFullTiebreak: true) : ExactFraudCount(query);

        return frauds;
    }

    [SkipLocalsInit]
    private byte RiskyFraudCount(ReadOnlySpan<short> query, bool allowFullTiebreak)
    {
        if (riskyPositions.Length < K)
            return ExactFraudCount(query);

        Span<long> topDist = stackalloc long[K];
        Span<int> topIds = stackalloc int[K];
        Span<byte> topLabels = stackalloc byte[K];
        topDist.Fill(long.MaxValue);
        topIds.Fill(int.MaxValue);

        ReadOnlySpan<ushort> neighborKeys = NeighborKeyOrders.AsSpan(BucketKey(query) * BucketCount, BucketCount);
        for (int neighborIndex = 0; neighborIndex < neighborKeys.Length; neighborIndex++)
        {
            int coarseKey = neighborKeys[neighborIndex];
            if (RiskyBucketLowerBound(coarseKey, query) >= topDist[^1])
                continue;

            int fineStart = riskyCoarseFineOffsets[coarseKey];
            int fineEnd = riskyCoarseFineOffsets[coarseKey + 1];
            for (int finePos = fineStart; finePos < fineEnd; finePos++)
            {
                int fineKey = riskyFineKeys[finePos];
                if (RiskyFineBucketLowerBound(fineKey, query) >= topDist[^1])
                    continue;

                int start = riskyFineBucketOffsets[fineKey];
                int end = riskyFineBucketOffsets[fineKey + 1];
                for (int pos = start; pos < end; pos++)
                    Consider(riskyPositions[pos], query, topDist, topIds, topLabels);
            }
        }

        if (topDist[^1] == long.MaxValue)
            return ExactFraudCount(query);

        byte frauds = CountFrauds(topLabels);
        return allowFullTiebreak && NeedsFullRiskyTiebreak(query, frauds) ? ExactFraudCount(query) : frauds;
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

    private static bool ShouldUseExactFallback(ReadOnlySpan<short> query, byte frauds, BucketSearchOptions options)
    {
        if (frauds > 0 && frauds < K)
            return options.ExactFallback;

        return options.RiskyFallback && IsStrongFallbackRisk(query, frauds);
    }

    private static bool IsStrongFallbackRisk(ReadOnlySpan<short> query, byte frauds)
    {
        if (frauds != 0 && frauds != K)
            return false;

        if (frauds == 0 && IsHighRiskOnlineFallback(query))
            return true;

        if (IsStrongProfileTiebreak(query, frauds))
            return true;

        if (frauds == K &&
            query[5] >= 600 && query[5] <= 850 &&
            query[9] == 0 &&
            query[10] == 0 &&
            query[11] == 0 &&
            query[12] <= 2000 &&
            query[0] >= 1100 && query[0] <= 1300 &&
            query[2] >= 4000 && query[2] <= 4600 &&
            query[7] >= 550 && query[7] <= 750 &&
            query[8] >= 2000 && query[8] <= 3000 &&
            query[13] >= 220 && query[13] <= 320)
        {
            return true;
        }

        return query[5] >= 0 &&
               query[10] == 0 &&
               query[0] >= 450 && query[0] <= 1100 &&
               query[2] >= 900 && query[2] <= 2500 &&
               query[7] >= 500 && query[7] <= 2000 &&
               query[8] >= 2000 && query[8] <= 4500;
    }

    private static bool IsStrongProfileTiebreak(ReadOnlySpan<short> query, byte frauds)
    {
        if (query[5] < 0 || query[13] > 220)
            return false;

        if (frauds == 0)
        {
            return
                (query[9] == 0 &&
                 query[10] > 0 &&
                 query[12] >= 7500 &&
                 query[0] >= 450 && query[0] <= 600 &&
                 query[2] >= 1000 && query[2] <= 1200 &&
                 query[7] >= 400 && query[7] <= 600 &&
                 query[8] >= 4000 && query[8] <= 5000) ||
                (query[9] > 0 &&
                 query[10] == 0 &&
                 query[12] <= 2500 &&
                 query[0] >= 2100 && query[0] <= 2300 &&
                 query[2] >= 4400 && query[2] <= 4900 &&
                 query[7] >= 700 && query[7] <= 950 &&
                 query[8] >= 2000 && query[8] <= 3000) ||
                (query[9] > 0 &&
                 query[10] == 0 &&
                 query[11] > 0 &&
                 query[12] >= 4000 && query[12] <= 5000 &&
                 query[0] >= 1200 && query[0] <= 1500 &&
                 query[2] >= 3300 && query[2] <= 3800 &&
                 query[7] >= 3300 && query[7] <= 3900 &&
                 query[8] >= 2000 && query[8] <= 3000);
        }

        return query[9] == 0 &&
               query[10] > 0 &&
               query[12] <= 2500 &&
               query[0] >= 2700 && query[0] <= 3000 &&
               query[2] >= 9000 &&
               query[7] >= 3500 && query[7] <= 4000 &&
               query[8] >= 2500 && query[8] <= 3500;
    }

    private static bool NeedsFullRiskyTiebreak(ReadOnlySpan<short> query, byte frauds)
    {
        if (query[5] < 0 || query[9] <= 0 || query[10] != 0)
            return false;

        if (frauds >= 3)
        {
            return query[11] == 0 &&
                   query[12] <= 1700 &&
                   query[0] >= 500 && query[0] <= 900 &&
                   query[2] >= 1000 && query[2] <= 2200 &&
                   query[7] >= 350 && query[7] <= 900 &&
                   query[8] >= 1800 && query[8] <= 3000;
        }

        return IsHighRiskOnlineFallback(query);
    }

    private static bool IsHighRiskOnlineFallback(ReadOnlySpan<short> query) =>
        query[12] >= 8000 &&
        query[1] >= 5500 &&
        query[6] >= 1000 && query[6] <= 1700 &&
        query[7] >= 300 && query[7] <= 4200 &&
        query[8] >= 3800 && query[8] <= 6000 &&
        ((query[0] >= 450 && query[0] <= 600 && query[2] <= 1200) ||
         (query[0] >= 2500 && query[0] <= 3100 && query[2] >= 9000));

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

    private static void BuildRiskyFallbackIndex(
        bool enabled,
        short* vectors,
        int count,
        int scale,
        out int[] positions,
        out int[] fineBucketOffsets,
        out int[] coarseFineOffsets,
        out int[] fineKeys)
    {
        if (!enabled)
        {
            positions = [];
            fineBucketOffsets = [];
            coarseFineOffsets = [];
            fineKeys = [];
            return;
        }

        RiskyFallbackFilter filter = RiskyFallbackFilter.FromEnvironment();
        var fineCounts = new int[RiskyFineBucketCount];
        for (int pos = 0; pos < count; pos++)
        {
            short* vector = vectors + (long)pos * Dims;
            if (IsRiskyFallbackReference(vector, filter))
                fineCounts[RiskyFineBucketKey(vector, scale)]++;
        }

        fineBucketOffsets = new int[RiskyFineBucketCount + 1];
        coarseFineOffsets = new int[BucketCount + 1];
        for (int fineKey = 0; fineKey < RiskyFineBucketCount; fineKey++)
        {
            fineBucketOffsets[fineKey + 1] = fineBucketOffsets[fineKey] + fineCounts[fineKey];
            if (fineCounts[fineKey] != 0)
                coarseFineOffsets[(fineKey >> RiskyFineExtraBits) + 1]++;
        }

        for (int key = 0; key < BucketCount; key++)
            coarseFineOffsets[key + 1] += coarseFineOffsets[key];

        fineKeys = new int[coarseFineOffsets[BucketCount]];
        var fineKeyPositions = new int[BucketCount];
        coarseFineOffsets.AsSpan(0, BucketCount).CopyTo(fineKeyPositions);
        for (int fineKey = 0; fineKey < RiskyFineBucketCount; fineKey++)
        {
            if (fineCounts[fineKey] == 0)
                continue;

            int coarseKey = fineKey >> RiskyFineExtraBits;
            fineKeys[fineKeyPositions[coarseKey]++] = fineKey;
        }

        positions = GC.AllocateUninitializedArray<int>(fineBucketOffsets[^1]);
        var writePositions = new int[RiskyFineBucketCount];
        fineBucketOffsets.AsSpan(0, RiskyFineBucketCount).CopyTo(writePositions);
        for (int pos = 0; pos < count; pos++)
        {
            short* vector = vectors + (long)pos * Dims;
            if (!IsRiskyFallbackReference(vector, filter))
                continue;

            int fineKey = RiskyFineBucketKey(vector, scale);
            positions[writePositions[fineKey]++] = pos;
        }
    }

    private static bool IsRiskyFallbackReference(short* vector, RiskyFallbackFilter filter)
    {
        short amount = vector[0];
        if (amount < filter.AmountMin || amount > filter.AmountMax)
            return false;

        short installments = vector[1];
        if (installments < filter.InstallmentsMin || installments > filter.InstallmentsMax)
            return false;

        if (vector[2] < filter.RatioMin)
            return false;

        short kmHome = vector[7];
        if (kmHome < filter.KmHomeMin || kmHome > filter.KmHomeMax)
            return false;

        short tx24h = vector[8];
        if (tx24h < filter.Tx24hMin || tx24h > filter.Tx24hMax)
            return false;

        short merchantAverage = vector[13];
        return merchantAverage >= filter.MerchantAverageMin && merchantAverage <= filter.MerchantAverageMax;
    }

    private static int RiskyFineBucketKey(short* vector, int scale)
    {
        int coarseKey = BucketKey(vector, scale);
        int extra = vector[9] > 0 ? 1 : 0;
        extra |= (vector[10] > 0 ? 1 : 0) << 1;
        extra |= (vector[11] > 0 ? 1 : 0) << 2;
        return (coarseKey << RiskyFineExtraBits) | extra;
    }

    private static int BucketKey(short* vector, int scale)
    {
        int amount = Bucket8(vector[0], scale);
        int ratio = Bucket8(vector[2], scale);
        int kmHome = Bucket8(vector[7], scale);
        int hour = Bucket4(vector[3], scale);
        int noLast = vector[5] < 0 ? 1 : 0;
        return amount | (ratio << 3) | (kmHome << 6) | (hour << 9) | (noLast << 11);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long RiskyBucketLowerBound(int key, ReadOnlySpan<short> query)
    {
        int amount = key & 7;
        int ratio = (key >> 3) & 7;
        int kmHome = (key >> 6) & 7;
        int hour = (key >> 9) & 3;
        int noLast = (key >> 11) & 1;

        long sum = 0;
        sum += BucketDistanceSquared(query[0], amount, 8, scale);
        sum += BucketDistanceSquared(query[2], ratio, 8, scale);
        sum += BucketDistanceSquared(query[7], kmHome, 8, scale);
        sum += BucketDistanceSquared(query[3], hour, 4, scale);
        sum += noLast == 0
            ? RangeDistanceSquared(query[5], 0, scale)
            : RangeDistanceSquared(query[5], -scale, -scale);
        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long RiskyFineBucketLowerBound(int fineKey, ReadOnlySpan<short> query)
    {
        int coarseKey = fineKey >> RiskyFineExtraBits;
        int extra = fineKey & ((1 << RiskyFineExtraBits) - 1);

        long sum = RiskyBucketLowerBound(coarseKey, query);
        sum += BinaryDistanceSquared(query[9], extra & 1, scale);
        sum += BinaryDistanceSquared(query[10], (extra >> 1) & 1, scale);
        sum += BinaryDistanceSquared(query[11], (extra >> 2) & 1, scale);
        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long BinaryDistanceSquared(short value, int bit, int scale) =>
        RangeDistanceSquared(value, bit == 0 ? 0 : scale, bit == 0 ? 0 : scale);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long BucketDistanceSquared(short value, int bucket, int divisions, int scale)
    {
        int min = bucket == 0 ? 0 : (bucket * (scale + 1) + divisions - 1) / divisions;
        int max = bucket == divisions - 1 ? scale : (((bucket + 1) * (scale + 1)) - 1) / divisions;
        return RangeDistanceSquared(value, min, max);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long RangeDistanceSquared(short value, int min, int max)
    {
        if (value < min)
        {
            long diff = (long)min - value;
            return diff * diff;
        }

        if (value > max)
        {
            long diff = (long)value - max;
            return diff * diff;
        }

        return 0;
    }

    private static int Bucket4(short value, int scale) => value <= 0 ? 0 : Math.Clamp(value * 4 / (scale + 1), 0, 3);

    private static int Bucket8(short value, int scale) => value <= 0 ? 0 : Math.Clamp(value * 8 / (scale + 1), 0, 7);

    private static bool IsRiskyFallbackEnabled()
    {
        string? fallback = Environment.GetEnvironmentVariable("BUCKET_EXACT_FALLBACK");
        if (string.Equals(fallback, "risky", StringComparison.OrdinalIgnoreCase) || fallback == "2")
            return true;

        string? enabled = Environment.GetEnvironmentVariable("BUCKET_RISKY_FALLBACK");
        return string.Equals(enabled, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase);
    }

    private readonly struct RiskyFallbackFilter
    {
        public readonly int AmountMin;
        public readonly int AmountMax;
        public readonly int InstallmentsMin;
        public readonly int InstallmentsMax;
        public readonly int RatioMin;
        public readonly int KmHomeMin;
        public readonly int KmHomeMax;
        public readonly int Tx24hMin;
        public readonly int Tx24hMax;
        public readonly int MerchantAverageMin;
        public readonly int MerchantAverageMax;

        private RiskyFallbackFilter(
            int amountMin,
            int amountMax,
            int installmentsMin,
            int installmentsMax,
            int ratioMin,
            int kmHomeMin,
            int kmHomeMax,
            int tx24hMin,
            int tx24hMax,
            int merchantAverageMin,
            int merchantAverageMax)
        {
            AmountMin = amountMin;
            AmountMax = amountMax;
            InstallmentsMin = installmentsMin;
            InstallmentsMax = installmentsMax;
            RatioMin = ratioMin;
            KmHomeMin = kmHomeMin;
            KmHomeMax = kmHomeMax;
            Tx24hMin = tx24hMin;
            Tx24hMax = tx24hMax;
            MerchantAverageMin = merchantAverageMin;
            MerchantAverageMax = merchantAverageMax;
        }

        public static RiskyFallbackFilter FromEnvironment() => new(
            EnvInt("BUCKET_RISKY_AMOUNT_MIN", "RISKY_AMOUNT_MIN", 400),
            EnvInt("BUCKET_RISKY_AMOUNT_MAX", "RISKY_AMOUNT_MAX", 3000),
            EnvInt("BUCKET_RISKY_INSTALLMENTS_MIN", "RISKY_INSTALLMENTS_MIN", 2200),
            EnvInt("BUCKET_RISKY_INSTALLMENTS_MAX", "RISKY_INSTALLMENTS_MAX", 6200),
            EnvInt("BUCKET_RISKY_RATIO_MIN", "RISKY_RATIO_MIN", 850),
            EnvInt("BUCKET_RISKY_KM_HOME_MIN", "RISKY_KM_HOME_MIN", 250),
            EnvInt("BUCKET_RISKY_KM_HOME_MAX", "RISKY_KM_HOME_MAX", 4000),
            EnvInt("BUCKET_RISKY_TX24H_MIN", "RISKY_TX24H_MIN", 1500),
            EnvInt("BUCKET_RISKY_TX24H_MAX", "RISKY_TX24H_MAX", 5800),
            EnvInt("BUCKET_RISKY_MERCHANT_AVG_MIN", "RISKY_MERCHANT_AVG_MIN", 0),
            EnvInt("BUCKET_RISKY_MERCHANT_AVG_MAX", "RISKY_MERCHANT_AVG_MAX", 420));

        private static int EnvInt(string name, string fallbackName, int fallback)
        {
            string? value = Environment.GetEnvironmentVariable(name) ?? Environment.GetEnvironmentVariable(fallbackName);
            return int.TryParse(value, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;
        }
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
