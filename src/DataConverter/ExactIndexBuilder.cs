/// <summary>
/// Builds Zan-style flat exact KNN reference storage.
/// </summary>
internal static class ExactIndexBuilder
{
    private const int Dims = 14;
    private const int Stride = 16;
    public const int Scale = 8192;

    public static void Write(string outputPath, float[] vectors, byte[] labels, int count, int maxRefs)
    {
        int targetCount = maxRefs <= 0 ? count : Math.Min(maxRefs, count);
        int targetFrauds = CountLabel(labels, targetCount, 1);
        int targetLegits = targetCount - targetFrauds;
        Console.WriteLine($"Exact build: refs={targetCount:N0}, fraud={targetFrauds:N0}, legit={targetLegits:N0}, scale={Scale}");

        short[] quantized = GC.AllocateUninitializedArray<short>(checked(targetCount * Stride));
        byte[] selectedLabels = GC.AllocateUninitializedArray<byte>(targetCount);

        for (int row = 0; row < targetCount; row++)
            WriteRow(vectors, labels, row, quantized, selectedLabels, row);

        using var stream = File.Create(outputPath);
        using var writer = new BinaryWriter(stream);
        writer.Write(targetCount);
        writer.Flush();
        stream.Write(MemoryMarshal.AsBytes(quantized.AsSpan()));
        stream.Write(selectedLabels);
    }

    private static int CountLabel(byte[] labels, int count, byte label)
    {
        int total = 0;
        for (int i = 0; i < count; i++)
        {
            if (labels[i] == label)
                total++;
        }

        return total;
    }

    private static void WriteRow(float[] vectors, byte[] labels, int sourceRow, short[] output, byte[] outputLabels, int outputRow)
    {
        int sourceBase = sourceRow * Dims;
        int targetBase = outputRow * Stride;
        for (int dim = 0; dim < Dims; dim++)
            output[targetBase + dim] = Quantize(vectors[sourceBase + dim]);

        output[targetBase + Dims] = 0;
        output[targetBase + Dims + 1] = 0;
        outputLabels[outputRow] = labels[sourceRow];
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
