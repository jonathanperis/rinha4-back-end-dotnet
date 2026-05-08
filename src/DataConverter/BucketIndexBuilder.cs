/// <summary>
/// Build-time configuration for the coarse bucket reference index.
/// </summary>
internal readonly record struct BucketBuildOptions(int Scale);

/// <summary>
/// Builds a bucket-ordered reference index used by the low-latency ANN scorer.
/// </summary>
internal static class BucketIndexBuilder
{
    private const int Magic = 0x4b435542; // BUCK
    private const int Version = 1;
    private const int Dims = 14;
    private const int BucketCount = 4096;
    private const int ProfileCount = 1 << 22;
    private const byte LegitMask = 1;
    private const byte FraudMask = 2;

    public static void Write(string outputPath, float[] vectors, byte[] labels, int count, BucketBuildOptions options)
    {
        int scale = Math.Clamp(options.Scale, 1, short.MaxValue);
        var quantized = GC.AllocateUninitializedArray<short>(count * Dims);
        var bucketCounts = new int[BucketCount];
        var profileCounts = new ushort[ProfileCount];
        var profileMasks = new byte[ProfileCount];

        Console.WriteLine("Quantizing bucket index...");
        for (int row = 0; row < count; row++)
        {
            int vectorBase = row * Dims;
            for (int dim = 0; dim < Dims; dim++)
                quantized[vectorBase + dim] = Quantize(vectors[vectorBase + dim], scale);

            int bucket = BucketKey(quantized.AsSpan(vectorBase, Dims), scale);
            bucketCounts[bucket]++;

            int profile = ProfileKey(quantized.AsSpan(vectorBase, Dims), scale);
            if (profileCounts[profile] < ushort.MaxValue)
                profileCounts[profile]++;
            profileMasks[profile] |= labels[row] == 0 ? LegitMask : FraudMask;
        }

        var offsets = new int[BucketCount + 1];
        for (int bucket = 0; bucket < BucketCount; bucket++)
            offsets[bucket + 1] = offsets[bucket] + bucketCounts[bucket];

        var positions = new int[BucketCount];
        offsets.AsSpan(0, BucketCount).CopyTo(positions);

        var idsOut = GC.AllocateUninitializedArray<int>(count);
        var labelsOut = GC.AllocateUninitializedArray<byte>(count);
        var vectorsOut = GC.AllocateUninitializedArray<short>(count * Dims);

        Console.WriteLine("Packing bucket index...");
        for (int row = 0; row < count; row++)
        {
            int sourceBase = row * Dims;
            int bucket = BucketKey(quantized.AsSpan(sourceBase, Dims), scale);
            int position = positions[bucket]++;
            int destinationBase = position * Dims;

            idsOut[position] = row;
            labelsOut[position] = labels[row];
            quantized.AsSpan(sourceBase, Dims).CopyTo(vectorsOut.AsSpan(destinationBase, Dims));
        }

        using var stream = File.Create(outputPath);
        using var writer = new BinaryWriter(stream);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(count);
        writer.Write(Dims);
        writer.Write(scale);
        writer.Write(BucketCount);
        writer.Write(ProfileCount);
        WriteInts(writer, offsets);
        WriteInts(writer, idsOut);
        writer.Write(labelsOut);
        WriteShorts(writer, vectorsOut);
        WriteUShorts(writer, profileCounts);
        writer.Write(profileMasks);
        writer.Flush();
    }

    private static short Quantize(float value, int scale)
    {
        if (value < -1.0f) value = -1.0f;
        if (value > 1.0f) value = 1.0f;
        return (short)MathF.Round(value * scale);
    }

    private static int BucketKey(ReadOnlySpan<short> vector, int scale)
    {
        int amount = Bucket8(vector[0], scale);
        int ratio = Bucket8(vector[2], scale);
        int kmHome = Bucket8(vector[7], scale);
        int hour = Bucket4(vector[3], scale);
        int noLast = vector[5] < 0 ? 1 : 0;
        return amount | (ratio << 3) | (kmHome << 6) | (hour << 9) | (noLast << 11);
    }

    private static int ProfileKey(ReadOnlySpan<short> vector, int scale)
    {
        int key = 0;
        key |= Bucket16(vector[2], scale);
        key |= Bucket8(vector[7], scale) << 4;
        key |= Bucket4(vector[8], scale) << 7;
        key |= Bucket4(vector[12], scale) << 9;
        key |= Bucket4(vector[0], scale) << 11;
        key |= (vector[5] < 0 ? 1 : 0) << 13;
        key |= (vector[9] > 0 ? 1 : 0) << 14;
        key |= (vector[10] > 0 ? 1 : 0) << 15;
        key |= (vector[11] > 0 ? 1 : 0) << 16;
        key |= Bucket4(vector[6], scale) << 17;
        key |= (vector[1] > scale / 10 ? 1 : 0) << 19;
        key |= Bucket4(vector[13], scale) << 20;
        return key;
    }

    private static int Bucket4(short value, int scale) => value <= 0 ? 0 : Math.Clamp(value * 4 / (scale + 1), 0, 3);

    private static int Bucket8(short value, int scale) => value <= 0 ? 0 : Math.Clamp(value * 8 / (scale + 1), 0, 7);

    private static int Bucket16(short value, int scale) => value <= 0 ? 0 : Math.Clamp(value * 16 / (scale + 1), 0, 15);

    private static void WriteInts(BinaryWriter writer, int[] values)
    {
        foreach (int value in values)
            writer.Write(value);
    }

    private static void WriteShorts(BinaryWriter writer, short[] values)
    {
        foreach (short value in values)
            writer.Write(value);
    }

    private static void WriteUShorts(BinaryWriter writer, ushort[] values)
    {
        foreach (ushort value in values)
            writer.Write(value);
    }
}
