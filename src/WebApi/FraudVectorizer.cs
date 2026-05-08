/// <summary>
/// Shared ISO timestamp parsing used by the API and tests.
/// </summary>
/// <remarks>
/// Manual parsing avoids <see cref="DateTime"/>, culture, timezone, and
/// allocation overhead on the fraud-score request path.
/// </remarks>
public static class FraudVectorizer
{
    /// <summary>
    /// Parses a fixed ISO UTC string into hour, weekday, and absolute minute stamp.
    /// </summary>
    /// <param name="value">Timestamp in the fixed <c>yyyy-MM-ddTHH:mm:ssZ</c> shape.</param>
    /// <param name="hour">Parsed UTC hour from 0 through 23.</param>
    /// <param name="dayOfWeek">Parsed Monday-based weekday from 0 through 6.</param>
    /// <param name="minuteStamp">Absolute minute stamp used for fast elapsed-time subtraction.</param>
    /// <remarks>Manual parsing avoids <see cref="DateTime"/>, culture, timezone, and allocation overhead.</remarks>
    public static void ParseIsoUtc(string value, out int hour, out int dayOfWeek, out int minuteStamp)
    {
        // Inputs are fixed ISO UTC strings. Manual parsing avoids DateTime,
        // culture, timezone, and allocation overhead in the hot path.
        int year = Parse4(value, 0);
        int month = Parse2(value, 5);
        int day = Parse2(value, 8);
        hour = Parse2(value, 11);
        int minute = Parse2(value, 14);

        int days = DaysFromCivil(year, month, day);
        dayOfWeek = Mod7(days + 3);
        minuteStamp = days * 1440 + hour * 60 + minute;
    }

    /// <summary>
    /// Parses a fixed UTF-8 ISO UTC span into hour, weekday, and absolute minute stamp.
    /// </summary>
    /// <param name="value">UTF-8 timestamp in the fixed <c>yyyy-MM-ddTHH:mm:ssZ</c> shape.</param>
    /// <param name="hour">Parsed UTC hour from 0 through 23.</param>
    /// <param name="dayOfWeek">Parsed Monday-based weekday from 0 through 6.</param>
    /// <param name="minuteStamp">Absolute minute stamp used for fast elapsed-time subtraction.</param>
    /// <remarks>This overload is the raw request hot path because <c>Utf8JsonReader</c> exposes UTF-8 spans.</remarks>
    public static void ParseIsoUtc(ReadOnlySpan<byte> value, out int hour, out int dayOfWeek, out int minuteStamp)
    {
        int year = Parse4(value, 0);
        int month = Parse2(value, 5);
        int day = Parse2(value, 8);
        hour = Parse2(value, 11);
        int minute = Parse2(value, 14);

        int days = DaysFromCivil(year, month, day);
        dayOfWeek = Mod7(days + 3);
        minuteStamp = days * 1440 + hour * 60 + minute;
    }

    /// <summary>
    /// Parses two ASCII digits from a string offset.
    /// </summary>
    /// <param name="value">String containing ASCII digits.</param>
    /// <param name="index">Offset of the first digit.</param>
    /// <returns>The two-digit integer value.</returns>
    private static int Parse2(string value, int index) =>
        (value[index] - '0') * 10 + value[index + 1] - '0';

    /// <summary>
    /// Parses four ASCII digits from a string offset.
    /// </summary>
    /// <param name="value">String containing ASCII digits.</param>
    /// <param name="index">Offset of the first digit.</param>
    /// <returns>The four-digit integer value.</returns>
    private static int Parse4(string value, int index) =>
        (value[index] - '0') * 1000 + (value[index + 1] - '0') * 100 + (value[index + 2] - '0') * 10 + value[index + 3] - '0';

    /// <summary>
    /// Parses two ASCII digits from a UTF-8 span offset.
    /// </summary>
    /// <param name="value">UTF-8 span containing ASCII digits.</param>
    /// <param name="index">Offset of the first digit.</param>
    /// <returns>The two-digit integer value.</returns>
    private static int Parse2(ReadOnlySpan<byte> value, int index) =>
        (value[index] - (byte)'0') * 10 + value[index + 1] - (byte)'0';

    /// <summary>
    /// Parses four ASCII digits from a UTF-8 span offset.
    /// </summary>
    /// <param name="value">UTF-8 span containing ASCII digits.</param>
    /// <param name="index">Offset of the first digit.</param>
    /// <returns>The four-digit integer value.</returns>
    private static int Parse4(ReadOnlySpan<byte> value, int index) =>
        (value[index] - (byte)'0') * 1000 + (value[index + 1] - (byte)'0') * 100 + (value[index + 2] - (byte)'0') * 10 + value[index + 3] - (byte)'0';

    /// <summary>
    /// Computes a positive modulo-seven result for weekday arithmetic.
    /// </summary>
    /// <param name="value">Value to reduce into the weekday range.</param>
    /// <returns>A value from <c>0</c> through <c>6</c>.</returns>
    private static int Mod7(int value)
    {
        int result = value % 7;
        return result < 0 ? result + 7 : result;
    }

    /// <summary>
    /// Converts a civil date to days since Unix epoch without DateTime.
    /// </summary>
    /// <param name="year">Gregorian year.</param>
    /// <param name="month">Gregorian month from 1 through 12.</param>
    /// <param name="day">Gregorian day of month.</param>
    /// <returns>Number of days since 1970-01-01.</returns>
    /// <remarks>Uses the civil-date algorithm from Howard Hinnant's date routines.</remarks>
    private static int DaysFromCivil(int year, int month, int day)
    {
        year -= month <= 2 ? 1 : 0;
        int era = (year >= 0 ? year : year - 399) / 400;
        uint yoe = (uint)(year - era * 400);
        uint monthPrime = (uint)(month + (month > 2 ? -3 : 9));
        uint dayOfYear = (153 * monthPrime + 2) / 5 + (uint)day - 1;
        uint dayOfEra = yoe * 365 + yoe / 4 - yoe / 100 + dayOfYear;
        return era * 146097 + (int)dayOfEra - 719468;
    }
}
