using System.Globalization;

VectorizationTestRunner.Run("maps Monday to zero and Sunday to one", () =>
{
    VectorizationTestRunner.AssertEqual(0.0f, FraudVectorizer.NormalizeDayOfWeek(DateTime.Parse("2026-03-09T12:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)));
    VectorizationTestRunner.AssertEqual(1.0f, FraudVectorizer.NormalizeDayOfWeek(DateTime.Parse("2026-03-15T12:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)));
});

VectorizationTestRunner.Run("parses ISO UTC hour and day without DateTime", () =>
{
    FraudVectorizer.ParseIsoUtc("2026-03-11T20:23:35Z", out int hour, out int dayOfWeek, out int minuteStamp);

    VectorizationTestRunner.AssertEqualInt(20, hour);
    VectorizationTestRunner.AssertEqualInt(2, dayOfWeek);
    VectorizationTestRunner.AssertEqualInt(0, minuteStamp - FraudVectorizer.IsoUtcMinuteStamp("2026-03-11T20:23:35Z"));
});

VectorizationTestRunner.Run("computes ISO UTC minute gaps across dates", () =>
{
    int current = FraudVectorizer.IsoUtcMinuteStamp("2026-03-11T00:03:00Z");
    int previous = FraudVectorizer.IsoUtcMinuteStamp("2026-03-10T23:58:00Z");

    VectorizationTestRunner.AssertEqualInt(5, current - previous);
});

VectorizationTestRunner.Run("detects unknown merchant from string array", () =>
{
    VectorizationTestRunner.AssertEqualInt(0, FraudVectorizer.UnknownMerchant("MERC-001", ["MERC-009", "MERC-001"]));
    VectorizationTestRunner.AssertEqualInt(1, FraudVectorizer.UnknownMerchant("MERC-002", ["MERC-009", "MERC-001"]));
});

VectorizationTestRunner.Run("builds vector group from last transaction and binary dimensions", () =>
{
    VectorizationTestRunner.AssertEqualInt(0b1111, FraudVectorizer.VectorGroup(42, 10_000, 10_000, 10_000));
    VectorizationTestRunner.AssertEqualInt(0b0000, FraudVectorizer.VectorGroup(-10_000, 0, 0, 0));
});

VectorizationTestRunner.Run("computes exact lower bound for vector groups", () =>
{
    int same = FraudVectorizer.GroupLowerBound(0b1111, 0b1111, 10_000, 1_000, 2_000);
    int missingLast = FraudVectorizer.GroupLowerBound(0b1111, 0b1110, 10_000, 1_000, 2_000);
    int binaryMismatch = FraudVectorizer.GroupLowerBound(0b1111, 0b0111, 10_000, 1_000, 2_000);

    VectorizationTestRunner.AssertEqualInt(0, same);
    VectorizationTestRunner.AssertEqualInt(265_000_000, missingLast);
    VectorizationTestRunner.AssertEqualInt(100_000_000, binaryMismatch);
});

VectorizationTestRunner.Run("builds fine group with continuous last transaction bins", () =>
{
    int group = FraudVectorizer.FineVectorGroup(1_000, 2_000, 3_000, 4_000, 0, 10_000, 0, 10_000);
    int same = FraudVectorizer.FineGroupLowerBound(group, group, 10_000, 1_000, 2_000);

    VectorizationTestRunner.AssertEqualInt(0, same);
});

VectorizationTestRunner.Run("computes fine group lower bound for neighboring bins", () =>
{
    int query = FraudVectorizer.FineVectorGroup(1_000, 2_000, 3_000, 4_000, 0, 10_000, 0, 10_000);
    int far = FraudVectorizer.FineVectorGroup(8_000, 8_000, 3_000, 4_000, 0, 10_000, 0, 10_000);

    int lower = FraudVectorizer.FineGroupLowerBound(query, far, 10_000, 1_000, 2_000);

    if (lower <= 0)
        throw new InvalidOperationException($"expected positive lower bound, got {lower}");
});

VectorizationTestRunner.Run("uses neutral risk for unlisted numeric MCC", () =>
{
    var risk = new float[10000];
    var known = new bool[10000];
    risk[5411] = 0.2f;
    known[5411] = true;

    VectorizationTestRunner.AssertEqual(0.2f, FraudVectorizer.LookupMccRisk("5411", risk, known));
    VectorizationTestRunner.AssertEqual(0.5f, FraudVectorizer.LookupMccRisk("9999", risk, known));
});

VectorizationTestRunner.Run("preserves listed zero MCC risk", () =>
{
    var risk = new float[10000];
    var known = new bool[10000];
    known[1234] = true;

    VectorizationTestRunner.AssertEqual(0.0f, FraudVectorizer.LookupMccRisk("1234", risk, known));
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
}
