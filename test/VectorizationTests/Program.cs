using System.Globalization;

Run("maps Monday to zero and Sunday to one", () =>
{
    AssertEqual(0.0f, FraudVectorizer.NormalizeDayOfWeek(DateTime.Parse("2026-03-09T12:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)));
    AssertEqual(1.0f, FraudVectorizer.NormalizeDayOfWeek(DateTime.Parse("2026-03-15T12:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)));
});

Run("parses ISO UTC hour and day without DateTime", () =>
{
    FraudVectorizer.ParseIsoUtc("2026-03-11T20:23:35Z", out int hour, out int dayOfWeek, out int minuteStamp);

    AssertEqualInt(20, hour);
    AssertEqualInt(2, dayOfWeek);
    AssertEqualInt(0, minuteStamp - FraudVectorizer.IsoUtcMinuteStamp("2026-03-11T20:23:35Z"));
});

Run("computes ISO UTC minute gaps across dates", () =>
{
    int current = FraudVectorizer.IsoUtcMinuteStamp("2026-03-11T00:03:00Z");
    int previous = FraudVectorizer.IsoUtcMinuteStamp("2026-03-10T23:58:00Z");

    AssertEqualInt(5, current - previous);
});

Run("detects unknown merchant from string array", () =>
{
    AssertEqualInt(0, FraudVectorizer.UnknownMerchant("MERC-001", ["MERC-009", "MERC-001"]));
    AssertEqualInt(1, FraudVectorizer.UnknownMerchant("MERC-002", ["MERC-009", "MERC-001"]));
});

Run("builds vector group from last transaction and binary dimensions", () =>
{
    AssertEqualInt(0b1111, FraudVectorizer.VectorGroup(42, 10_000, 10_000, 10_000));
    AssertEqualInt(0b0000, FraudVectorizer.VectorGroup(-10_000, 0, 0, 0));
});

Run("computes exact lower bound for vector groups", () =>
{
    int same = FraudVectorizer.GroupLowerBound(0b1111, 0b1111, 10_000, 1_000, 2_000);
    int missingLast = FraudVectorizer.GroupLowerBound(0b1111, 0b1110, 10_000, 1_000, 2_000);
    int binaryMismatch = FraudVectorizer.GroupLowerBound(0b1111, 0b0111, 10_000, 1_000, 2_000);

    AssertEqualInt(0, same);
    AssertEqualInt(265_000_000, missingLast);
    AssertEqualInt(100_000_000, binaryMismatch);
});

Run("builds fine group with continuous last transaction bins", () =>
{
    int group = FraudVectorizer.FineVectorGroup(1_000, 2_000, 3_000, 4_000, 0, 10_000, 0, 10_000);
    int same = FraudVectorizer.FineGroupLowerBound(group, group, 10_000, 1_000, 2_000);

    AssertEqualInt(0, same);
});

Run("computes fine group lower bound for neighboring bins", () =>
{
    int query = FraudVectorizer.FineVectorGroup(1_000, 2_000, 3_000, 4_000, 0, 10_000, 0, 10_000);
    int far = FraudVectorizer.FineVectorGroup(8_000, 8_000, 3_000, 4_000, 0, 10_000, 0, 10_000);

    int lower = FraudVectorizer.FineGroupLowerBound(query, far, 10_000, 1_000, 2_000);

    if (lower <= 0)
        throw new InvalidOperationException($"expected positive lower bound, got {lower}");
});

Run("uses neutral risk for unlisted numeric MCC", () =>
{
    var risk = new float[10000];
    var known = new bool[10000];
    risk[5411] = 0.2f;
    known[5411] = true;

    AssertEqual(0.2f, FraudVectorizer.LookupMccRisk("5411", risk, known));
    AssertEqual(0.5f, FraudVectorizer.LookupMccRisk("9999", risk, known));
});

Run("preserves listed zero MCC risk", () =>
{
    var risk = new float[10000];
    var known = new bool[10000];
    known[1234] = true;

    AssertEqual(0.0f, FraudVectorizer.LookupMccRisk("1234", risk, known));
});

static void Run(string name, Action test)
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

static void AssertEqual(float expected, float actual)
{
    if (MathF.Abs(expected - actual) > 0.0001f)
        throw new InvalidOperationException($"expected {expected.ToString(CultureInfo.InvariantCulture)}, got {actual.ToString(CultureInfo.InvariantCulture)}");
}

static void AssertEqualInt(int expected, int actual)
{
    if (expected != actual)
        throw new InvalidOperationException($"expected {expected.ToString(CultureInfo.InvariantCulture)}, got {actual.ToString(CultureInfo.InvariantCulture)}");
}
