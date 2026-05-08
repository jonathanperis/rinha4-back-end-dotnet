VectorizationTestRunner.Run("parses UTF-8 ISO UTC hour and weekday", () =>
{
    FraudVectorizer.ParseIsoUtc("2026-03-11T20:23:35Z"u8, out int hour, out int dayOfWeek, out int minuteStamp);

    VectorizationTestRunner.AssertEqualInt(20, hour);
    VectorizationTestRunner.AssertEqualInt(2, dayOfWeek);
    if (minuteStamp <= 0)
        throw new InvalidOperationException($"expected positive minute stamp, got {minuteStamp}");
});

VectorizationTestRunner.Run("computes UTF-8 ISO UTC minute gaps across dates", () =>
{
    FraudVectorizer.ParseIsoUtc("2026-03-11T00:03:00Z"u8, out _, out _, out int current);
    FraudVectorizer.ParseIsoUtc("2026-03-10T23:58:00Z"u8, out _, out _, out int previous);

    VectorizationTestRunner.AssertEqualInt(5, current - previous);
});

VectorizationTestRunner.Run("parses string ISO UTC without allocation-heavy DateTime", () =>
{
    FraudVectorizer.ParseIsoUtc("2026-03-15T07:01:00Z", out int hour, out int dayOfWeek, out int minuteStamp);

    VectorizationTestRunner.AssertEqualInt(7, hour);
    VectorizationTestRunner.AssertEqualInt(6, dayOfWeek);
    if (minuteStamp <= 0)
        throw new InvalidOperationException($"expected positive minute stamp, got {minuteStamp}");
});

VectorizationTestRunner.Run("loads IVF index and repairs boundary fraud counts", () =>
{
    string path = Path.Combine(Path.GetTempPath(), $"rinha-ivf-test-{Guid.NewGuid():N}.bin");
    try
    {
        for (byte frauds = 1; frauds <= 4; frauds++)
            IvfRepairAssertions.AssertBoundaryRepair(path, frauds, 5, frauds, 5);

        IvfRepairAssertions.AssertBoundaryRepair(path, 0, 5, 0, 0);
        IvfRepairAssertions.AssertBoundaryRepair(path, 5, 0, 5, 5);
    }
    finally
    {
        if (File.Exists(path))
            File.Delete(path);
    }
});

VectorizationTestRunner.Run("reads one-pass full repair as IVF default", () =>
{
    using var env = new ScopedEnvironment(
        ("IVF_BOUNDARY_FULL", null),
        ("IVF_REPAIR_MIN_FRAUDS", null),
        ("IVF_REPAIR_MAX_FRAUDS", null));

    IvfSearchOptions options = IvfSearchOptions.FromEnvironment();

    VectorizationTestRunner.AssertEqualInt(0, options.BoundaryFull ? 1 : 0);
    VectorizationTestRunner.AssertEqualInt(0, options.RepairMinFrauds);
    VectorizationTestRunner.AssertEqualInt(5, options.RepairMaxFrauds);
});

/// <summary>
/// Minimal test runner and assertion helpers for vectorization behavior.
/// </summary>
/// <remarks>
/// Kept framework-free so these checks run quickly with <c>dotnet run</c> and
/// remain portable inside the competition-oriented repository.
/// </remarks>
internal static class VectorizationTestRunner
{
    /// <summary>
    /// Executes one named test and records failure through <see cref="Environment.ExitCode"/>.
    /// </summary>
    /// <param name="name">Human-readable test name printed with PASS or FAIL.</param>
    /// <param name="test">Action containing assertions for one behavior.</param>
    public static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    /// <summary>
    /// Asserts that two floats match within the vectorization tolerance.
    /// </summary>
    /// <param name="expected">Expected float value.</param>
    /// <param name="actual">Actual float value produced by the code under test.</param>
    /// <exception cref="InvalidOperationException">Thrown when values differ by more than <c>0.0001</c>.</exception>
    public static void AssertEqual(float expected, float actual)
    {
        if (MathF.Abs(expected - actual) > 0.0001f)
            throw new InvalidOperationException($"expected {expected.ToString(CultureInfo.InvariantCulture)}, got {actual.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>
    /// Asserts that two integer values match exactly.
    /// </summary>
    /// <param name="expected">Expected integer value.</param>
    /// <param name="actual">Actual integer value produced by the code under test.</param>
    /// <exception cref="InvalidOperationException">Thrown when values differ.</exception>
    public static void AssertEqualInt(int expected, int actual)
    {
        if (expected != actual)
            throw new InvalidOperationException($"expected {expected.ToString(CultureInfo.InvariantCulture)}, got {actual.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>
    /// Asserts that two integer values differ.
    /// </summary>
    /// <param name="left">First integer value.</param>
    /// <param name="right">Second integer value.</param>
    /// <exception cref="InvalidOperationException">Thrown when both values are equal.</exception>
    public static void AssertNotEqualInt(int left, int right)
    {
        if (left == right)
            throw new InvalidOperationException($"expected different values, got {left.ToString(CultureInfo.InvariantCulture)}");
    }
}

/// <summary>
/// Assertions for the IVF boundary-repair policy.
/// </summary>
internal static class IvfRepairAssertions
{
    /// <summary>
    /// Writes one fixture, loads it, and compares fast-only and repaired fraud counts.
    /// </summary>
    /// <param name="path">Reusable fixture path.</param>
    /// <param name="firstPassFrauds">Fraud count returned by the nearest centroid cluster.</param>
    /// <param name="repairFrauds">Fraud count returned if bbox repair scans the second cluster.</param>
    /// <param name="expectedFastOnly">Expected count with no boundary or bbox repair.</param>
    /// <param name="expectedRepaired">Expected count with boundary and bbox repair enabled for counts <c>1..4</c>.</param>
    public static void AssertBoundaryRepair(string path, byte firstPassFrauds, byte repairFrauds, byte expectedFastOnly, byte expectedRepaired)
    {
        IvfTestIndex.Write(path, firstPassFrauds, repairFrauds);
        if (!IvfIndex.TryLoad(path, out IvfIndex? index, out string error) || index is null)
            throw new InvalidOperationException(error);

        Span<float> query = stackalloc float[14];
        Span<short> quantized = stackalloc short[16];

        byte fastOnly = index.FraudCount(quantized, new IvfSearchOptions(1, 1, false, false, 1, 4));
        byte repaired = index.FraudCount(quantized, new IvfSearchOptions(1, 1, true, true, 1, 4));

        VectorizationTestRunner.AssertEqualInt(expectedFastOnly, fastOnly);
        VectorizationTestRunner.AssertEqualInt(expectedRepaired, repaired);
    }
}

/// <summary>
/// Restores environment variables after one test case.
/// </summary>
/// <remarks>
/// Tests exercise runtime knobs that the production scorer reads from process
/// environment. Restoring values keeps one assertion from leaking into another.
/// </remarks>
internal sealed class ScopedEnvironment : IDisposable
{
    private readonly (string Name, string? Value)[] previousValues;

    /// <summary>
    /// Sets environment variables until <see cref="Dispose"/> is called.
    /// </summary>
    /// <param name="values">Variable names and temporary values.</param>
    public ScopedEnvironment(params (string Name, string? Value)[] values)
    {
        previousValues = new (string Name, string? Value)[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            previousValues[i] = (values[i].Name, Environment.GetEnvironmentVariable(values[i].Name));
            Environment.SetEnvironmentVariable(values[i].Name, values[i].Value);
        }
    }

    /// <summary>
    /// Restores all variables captured by the constructor.
    /// </summary>
    public void Dispose()
    {
        foreach ((string name, string? value) in previousValues)
            Environment.SetEnvironmentVariable(name, value);
    }
}

/// <summary>
/// Writes a tiny deterministic IVF fixture for scorer behavior tests.
/// </summary>
/// <remarks>
/// The fixture makes the nearest centroid cluster return a boundary count of
/// configurable frauds, then gives bbox repair a second cluster that contains
/// closer vectors. This validates <c>nprobe=1</c>, boundary repair, and bbox repair.
/// </remarks>
internal static class IvfTestIndex
{
    private const int Magic = 0x33465649;
    private const int Count = 11;
    private const int Clusters = 2;
    private const int Dims = 14;
    private const int Scale = 1000;
    private const int BlockLanes = 8;
    private const int TotalBlocks = 2;

    /// <summary>
    /// Writes the test index to disk.
    /// </summary>
    /// <param name="path">Destination binary path.</param>
    /// <param name="firstPassFrauds">Fraud labels in the nearest centroid block.</param>
    /// <param name="repairFrauds">Fraud labels in the bbox-repair block.</param>
    public static void Write(string path, byte firstPassFrauds, byte repairFrauds)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write(Magic);
        writer.Write(Count);
        writer.Write(Clusters);
        writer.Write(Dims);
        writer.Write(Scale);
        writer.Write(BlockLanes);
        writer.Write(TotalBlocks);

        WriteCentroids(writer);
        WriteBoundingBoxes(writer);
        WriteOffsets(writer);
        WriteLabels(writer, firstPassFrauds, repairFrauds);
        WriteIds(writer);
        WriteBlocks(writer);
    }

    /// <summary>
    /// Writes dimension-major centroids so cluster zero is selected by <c>nprobe=1</c>.
    /// </summary>
    /// <param name="writer">Binary writer positioned after the header.</param>
    private static void WriteCentroids(BinaryWriter writer)
    {
        for (int dim = 0; dim < Dims; dim++)
        {
            writer.Write((short)0);
            writer.Write((short)Scale);
        }
    }

    /// <summary>
    /// Writes bounding boxes that allow the second cluster to repair the first-pass result.
    /// </summary>
    /// <param name="writer">Binary writer positioned after centroids.</param>
    private static void WriteBoundingBoxes(BinaryWriter writer)
    {
        for (int i = 0; i < Clusters * Dims; i++)
            writer.Write((short)0);

        for (int i = 0; i < Clusters * Dims; i++)
            writer.Write((short)200);
    }

    /// <summary>
    /// Writes one block per cluster.
    /// </summary>
    /// <param name="writer">Binary writer positioned after bounding boxes.</param>
    private static void WriteOffsets(BinaryWriter writer)
    {
        writer.Write(0);
        writer.Write(1);
        writer.Write(2);
    }

    /// <summary>
    /// Writes padded labels for both blocks.
    /// </summary>
    /// <param name="writer">Binary writer positioned after offsets.</param>
    /// <param name="firstPassFrauds">Fraud labels in the nearest centroid block.</param>
    /// <param name="repairFrauds">Fraud labels in the bbox-repair block.</param>
    private static void WriteLabels(BinaryWriter writer, byte firstPassFrauds, byte repairFrauds)
    {
        byte[] labels = new byte[TotalBlocks * BlockLanes];
        for (int i = 0; i < Math.Min(firstPassFrauds, (byte)5); i++)
            labels[i] = 1;
        labels[5] = 1;

        for (int i = 0; i < Math.Min(repairFrauds, (byte)5); i++)
            labels[BlockLanes + i] = 1;

        writer.Write(labels);
    }

    /// <summary>
    /// Writes original ids, with negative ids marking padded lanes.
    /// </summary>
    /// <param name="writer">Binary writer positioned after labels.</param>
    private static void WriteIds(BinaryWriter writer)
    {
        int[] ids =
        [
            0, 1, 2, 3, 4, 5, -1, -1,
            6, 7, 8, 9, 10, -1, -1, -1
        ];

        foreach (int id in ids)
            writer.Write(id);
    }

    /// <summary>
    /// Writes two packed dimension-major blocks.
    /// </summary>
    /// <param name="writer">Binary writer positioned after ids.</param>
    private static void WriteBlocks(BinaryWriter writer)
    {
        short[] lane0 = [10, 11, 12, 13, 14, 15, short.MaxValue, short.MaxValue];
        short[] lane1 = [0, 0, 0, 0, 0, short.MaxValue, short.MaxValue, short.MaxValue];

        for (int dim = 0; dim < Dims; dim++)
            foreach (short value in lane0)
                writer.Write(value);

        for (int dim = 0; dim < Dims; dim++)
            foreach (short value in lane1)
                writer.Write(value);
    }
}
