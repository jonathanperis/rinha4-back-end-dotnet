/// <summary>
/// Builds Zan-style flat exact KNN reference storage.
/// </summary>
internal static class ExactIndexBuilder
{
    private const int Dims = 14;
    private const int Stride = 16;
    public const int Scale = 8192;

    public static void Write(string outputPath, float[] vectors, byte[] labels, int count)
    {
        short[] quantized = GC.AllocateUninitializedArray<short>(checked(count * Stride));
        for (int row = 0; row < count; row++)
        {
            int sourceBase = row * Dims;
            int targetBase = row * Stride;
            for (int dim = 0; dim < Dims; dim++)
                quantized[targetBase + dim] = Quantize(vectors[sourceBase + dim]);

            quantized[targetBase + Dims] = 0;
            quantized[targetBase + Dims + 1] = 0;
        }

        using var stream = File.Create(outputPath);
        using var writer = new BinaryWriter(stream);
        writer.Write(count);
        writer.Flush();
        stream.Write(MemoryMarshal.AsBytes(quantized.AsSpan()));
        stream.Write(labels);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short Quantize(float value)
    {
        float q = MathF.Round(value * Scale);
        if (q > short.MaxValue) q = short.MaxValue;
        if (q < short.MinValue) q = short.MinValue;
        return (short)q;
    }
}
