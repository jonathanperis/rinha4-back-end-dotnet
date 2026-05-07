using System.Globalization;

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

VectorizationTestRunner.Run("parses fallback string ISO UTC without allocation-heavy DateTime", () =>
{
    FraudVectorizer.ParseIsoUtc("2026-03-15T07:01:00Z", out int hour, out int dayOfWeek, out int minuteStamp);

    VectorizationTestRunner.AssertEqualInt(7, hour);
    VectorizationTestRunner.AssertEqualInt(6, dayOfWeek);
    if (minuteStamp <= 0)
        throw new InvalidOperationException($"expected positive minute stamp, got {minuteStamp}");
});

VectorizationTestRunner.Run("builds stable fine groups for bucket lookup", () =>
{
    int group = FraudVectorizer.FineVectorGroup(1_000, 2_000, 3_000, 4_000, 0, 10_000, 0, 10_000);
    int repeat = FraudVectorizer.FineVectorGroup(1_000, 2_000, 3_000, 4_000, 0, 10_000, 0, 10_000);

    VectorizationTestRunner.AssertEqualInt(group, repeat);
});

VectorizationTestRunner.Run("separates fine groups by continuous bins and flags", () =>
{
    int query = FraudVectorizer.FineVectorGroup(1_000, 2_000, 3_000, 4_000, 0, 10_000, 0, 10_000);
    int farBins = FraudVectorizer.FineVectorGroup(8_000, 8_000, 3_000, 4_000, 0, 10_000, 0, 10_000);
    int farFlag = FraudVectorizer.FineVectorGroup(1_000, 2_000, 3_000, 4_000, 10_000, 10_000, 0, 10_000);

    VectorizationTestRunner.AssertNotEqualInt(query, farBins);
    VectorizationTestRunner.AssertNotEqualInt(query, farFlag);
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
