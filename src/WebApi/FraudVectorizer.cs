/// <summary>
/// Shared feature-vector grouping and ISO timestamp parsing used by converter,
/// API, and tests.
/// </summary>
/// <remarks>
/// This class is the contract between <c>DataConverter</c> and <c>WebApi</c>.
/// Any grouping change must remain deterministic across both processes because
/// <c>references.bin</c> stores labels grouped by the exact id returned here.
/// </remarks>
public static class FraudVectorizer
{
    // Fine bucket layout: coarse risk flags, last-transaction bins, amount bin,
    // and home-distance bin. This gives an O(1) lookup key for default scoring.
    /// <summary>
    /// Number of bins used for last-transaction continuous dimensions.
    /// </summary>
    public const int FineBins = 32;

    /// <summary>
    /// Number of bins used for additional amount and home-distance dimensions.
    /// </summary>
    public const int ExtraBins = 16;

    /// <summary>
    /// Total fine-bucket count used by <c>references.bin</c> group offsets and response indexes.
    /// </summary>
    public const int FineGroupCount = 16 * FineBins * FineBins * ExtraBins * ExtraBins;

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
    /// Packs the coarse last-transaction and binary fraud dimensions into a group id.
    /// </summary>
    /// <param name="minutesSinceLast">Quantized minutes since last transaction; negative means no last transaction.</param>
    /// <param name="isOnline">Quantized online flag where positive means true.</param>
    /// <param name="cardPresent">Quantized card-present flag where positive means true.</param>
    /// <param name="unknownMerchant">Quantized unknown-merchant flag where positive means true.</param>
    /// <returns>A four-bit group id from <c>0</c> through <c>15</c>.</returns>
    private static int VectorGroup(int minutesSinceLast, int isOnline, int cardPresent, int unknownMerchant)
    {
        // Coarse group packs four binary dimensions into one nibble.
        int group = minutesSinceLast >= 0 ? 1 : 0;
        if (isOnline > 0) group |= 2;
        if (cardPresent > 0) group |= 4;
        if (unknownMerchant > 0) group |= 8;
        return group;
    }

    /// <summary>
    /// Packs coarse flags plus continuous bins into the production fine-bucket id.
    /// </summary>
    /// <param name="minutesSinceLast">Quantized minutes since last transaction.</param>
    /// <param name="kmFromLast">Quantized distance from the last transaction.</param>
    /// <param name="amount">Quantized transaction amount.</param>
    /// <param name="kmFromHome">Quantized distance from customer home.</param>
    /// <param name="isOnline">Quantized online flag where positive means true.</param>
    /// <param name="cardPresent">Quantized card-present flag where positive means true.</param>
    /// <param name="unknownMerchant">Quantized unknown-merchant flag where positive means true.</param>
    /// <param name="scale">Quantization scale used by both converter and API.</param>
    /// <returns>A stable fine-bucket id used to index grouped labels and response majorities.</returns>
    public static int FineVectorGroup(int minutesSinceLast, int kmFromLast, int amount, int kmFromHome, int isOnline, int cardPresent, int unknownMerchant, int scale)
    {
        // Continuous dimensions are binned after coarse flags. The resulting
        // integer is stable across converter, API, and tests.
        int coarse = VectorGroup(minutesSinceLast, isOnline, cardPresent, unknownMerchant);
        int minuteBin = (coarse & 1) != 0 ? ContinuousBin(minutesSinceLast, scale) : 0;
        int kmBin = (coarse & 1) != 0 ? ContinuousBin(kmFromLast, scale) : 0;
        int amountBin = ExtraBin(amount, scale);
        int homeBin = ExtraBin(kmFromHome, scale);
        return (((coarse * FineBins + minuteBin) * FineBins + kmBin) * ExtraBins + amountBin) * ExtraBins + homeBin;
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
    /// Maps a normalized continuous feature into a fine-bucket bin.
    /// </summary>
    /// <param name="value">Quantized feature value.</param>
    /// <param name="scale">Quantization scale representing the maximum normalized value.</param>
    /// <returns>A bin id from <c>0</c> through <see cref="FineBins"/> minus one.</returns>
    private static int ContinuousBin(int value, int scale)
    {
        if (value <= 0) return 0;
        if (value >= scale) return FineBins - 1;
        return value * FineBins / (scale + 1);
    }

    /// <summary>
    /// Maps an extra continuous feature into its smaller bucket range.
    /// </summary>
    /// <param name="value">Quantized feature value.</param>
    /// <param name="scale">Quantization scale representing the maximum normalized value.</param>
    /// <returns>A bin id from <c>0</c> through <see cref="ExtraBins"/> minus one.</returns>
    private static int ExtraBin(int value, int scale)
    {
        if (value <= 0) return 0;
        if (value >= scale) return ExtraBins - 1;
        return value * ExtraBins / (scale + 1);
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
