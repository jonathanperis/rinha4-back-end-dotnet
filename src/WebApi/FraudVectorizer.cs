/// <summary>
/// Shared feature-vector grouping, ISO timestamp parsing, and low-level risk
/// helpers used by converter, API, tests, and validator.
/// </summary>
/// <remarks>
/// This class is the contract between <c>DataConverter</c> and <c>WebApi</c>.
/// Any grouping change must remain deterministic across both processes because
/// <c>references.bin</c> stores vectors grouped by the exact id returned here.
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
    /// Normalizes a DateTime weekday so Monday maps to 0 and Sunday maps to 1.
    /// </summary>
    /// <param name="requestedAt">Timestamp whose <see cref="DateTime.DayOfWeek"/> should be normalized.</param>
    /// <returns>A float in the <c>0..1</c> range, with Monday as <c>0</c> and Sunday as <c>1</c>.</returns>
    public static float NormalizeDayOfWeek(DateTime requestedAt) =>
        (((int)requestedAt.DayOfWeek + 6) % 7) / 6.0f;

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
    /// Parses a fixed ISO UTC string and returns only the absolute minute stamp.
    /// </summary>
    /// <param name="value">Timestamp in the fixed <c>yyyy-MM-ddTHH:mm:ssZ</c> shape.</param>
    /// <returns>Absolute minute stamp from the Unix epoch calendar basis.</returns>
    public static int IsoUtcMinuteStamp(string value)
    {
        ParseIsoUtc(value, out _, out _, out int minuteStamp);
        return minuteStamp;
    }

    /// <summary>
    /// Returns 1 when a merchant is absent from the customer's known merchant list.
    /// </summary>
    /// <param name="merchantId">Merchant id from the current request.</param>
    /// <param name="knownMerchants">Customer known merchant ids.</param>
    /// <returns><c>0</c> when known, otherwise <c>1</c> for vector binary feature use.</returns>
    public static int UnknownMerchant(string merchantId, string[] knownMerchants)
    {
        for (int i = 0; i < knownMerchants.Length; i++)
        {
            if (knownMerchants[i] == merchantId)
                return 0;
        }

        return 1;
    }

    /// <summary>
    /// Packs the coarse last-transaction and binary fraud dimensions into a group id.
    /// </summary>
    /// <param name="minutesSinceLast">Quantized minutes since last transaction; negative means no last transaction.</param>
    /// <param name="isOnline">Quantized online flag where positive means true.</param>
    /// <param name="cardPresent">Quantized card-present flag where positive means true.</param>
    /// <param name="unknownMerchant">Quantized unknown-merchant flag where positive means true.</param>
    /// <returns>A four-bit group id from <c>0</c> through <c>15</c>.</returns>
    public static int VectorGroup(int minutesSinceLast, int isOnline, int cardPresent, int unknownMerchant)
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
    /// <returns>A stable fine-bucket id used to index grouped vectors and response majorities.</returns>
    public static int FineVectorGroup(int minutesSinceLast, int kmFromLast, int amount, int kmFromHome, int isOnline, int cardPresent, int unknownMerchant, int scale)
    {
        // Continuous dimensions are binned after coarse flags. The resulting
        // integer is stable across converter, API, tests, and validator.
        int coarse = VectorGroup(minutesSinceLast, isOnline, cardPresent, unknownMerchant);
        int minuteBin = (coarse & 1) != 0 ? ContinuousBin(minutesSinceLast, scale) : 0;
        int kmBin = (coarse & 1) != 0 ? ContinuousBin(kmFromLast, scale) : 0;
        int amountBin = ExtraBin(amount, scale);
        int homeBin = ExtraBin(kmFromHome, scale);
        return (((coarse * FineBins + minuteBin) * FineBins + kmBin) * ExtraBins + amountBin) * ExtraBins + homeBin;
    }

    /// <summary>
    /// Computes a coarse squared-distance lower bound between a query and group.
    /// </summary>
    /// <param name="queryGroup">Coarse group id for the query vector.</param>
    /// <param name="group">Coarse group id for the candidate bucket.</param>
    /// <param name="scale">Quantization scale representing a full binary-feature mismatch.</param>
    /// <param name="queryMinutesSinceLast">Query recency value used when last-transaction presence differs.</param>
    /// <param name="queryKmFromLast">Query last-transaction distance used when presence differs.</param>
    /// <returns>Minimum possible squared distance between the query and any vector in the coarse group.</returns>
    public static int GroupLowerBound(int queryGroup, int group, int scale, int queryMinutesSinceLast, int queryKmFromLast)
    {
        int lower = 0;

        if (((queryGroup ^ group) & 1) != 0)
        {
            int minuteDiff = (queryGroup & 1) != 0 ? queryMinutesSinceLast + scale : scale;
            int kmDiff = (queryGroup & 1) != 0 ? queryKmFromLast + scale : scale;
            lower = SaturatingAddSquare(lower, minuteDiff, int.MaxValue);
            lower = SaturatingAddSquare(lower, kmDiff, int.MaxValue);
        }

        if (((queryGroup ^ group) & 2) != 0)
            lower = SaturatingAddSquare(lower, scale, int.MaxValue);
        if (((queryGroup ^ group) & 4) != 0)
            lower = SaturatingAddSquare(lower, scale, int.MaxValue);
        if (((queryGroup ^ group) & 8) != 0)
            lower = SaturatingAddSquare(lower, scale, int.MaxValue);

        return lower;
    }

    /// <summary>
    /// Computes a squared-distance lower bound between a query and fine bucket.
    /// </summary>
    /// <param name="queryGroup">Fine-bucket id for the query vector.</param>
    /// <param name="group">Fine-bucket id for the candidate bucket.</param>
    /// <param name="scale">Quantization scale used to derive bin boundaries.</param>
    /// <param name="queryMinutesSinceLast">Query recency value used for bin-distance calculation.</param>
    /// <param name="queryKmFromLast">Query distance-from-last value used for bin-distance calculation.</param>
    /// <returns>Minimum possible squared distance between the query and any vector in the fine bucket.</returns>
    /// <remarks>This is used by tests and exact-search experiments, not by the default O(1) production scorer.</remarks>
    public static int FineGroupLowerBound(int queryGroup, int group, int scale, int queryMinutesSinceLast, int queryKmFromLast)
    {
        // Used by tests/experiments for exact-search pruning. It estimates the
        // minimum possible squared distance from a query to a fine bucket.
        DecodeFineGroup(queryGroup, out int queryCoarse, out _, out _);
        DecodeFineGroup(group, out int coarse, out int minuteBin, out int kmBin);

        int lower = 0;

        if (((queryCoarse ^ coarse) & 1) != 0)
        {
            if ((queryCoarse & 1) != 0)
            {
                lower = SaturatingAddSquare(lower, queryMinutesSinceLast + scale, int.MaxValue);
                lower = SaturatingAddSquare(lower, queryKmFromLast + scale, int.MaxValue);
            }
            else
            {
                lower = SaturatingAddSquare(lower, scale + BinLow(minuteBin, scale), int.MaxValue);
                lower = SaturatingAddSquare(lower, scale + BinLow(kmBin, scale), int.MaxValue);
            }
        }
        else if ((queryCoarse & 1) != 0)
        {
            lower = SaturatingAddSquare(lower, DistanceToBin(queryMinutesSinceLast, minuteBin, scale), int.MaxValue);
            lower = SaturatingAddSquare(lower, DistanceToBin(queryKmFromLast, kmBin, scale), int.MaxValue);
        }

        if (((queryCoarse ^ coarse) & 2) != 0)
            lower = SaturatingAddSquare(lower, scale, int.MaxValue);
        if (((queryCoarse ^ coarse) & 4) != 0)
            lower = SaturatingAddSquare(lower, scale, int.MaxValue);
        if (((queryCoarse ^ coarse) & 8) != 0)
            lower = SaturatingAddSquare(lower, scale, int.MaxValue);

        return lower;
    }

    /// <summary>
    /// Looks up MCC risk and falls back to neutral risk for unknown or invalid codes.
    /// </summary>
    /// <param name="mcc">Merchant category code as supplied by source JSON.</param>
    /// <param name="riskByCode">Flat risk table indexed by numeric MCC code.</param>
    /// <param name="knownRiskByCode">Presence table that distinguishes unknown MCC from listed zero risk.</param>
    /// <returns>The listed MCC risk, or neutral <c>0.5</c> for invalid/unlisted codes.</returns>
    public static float LookupMccRisk(string mcc, float[] riskByCode, bool[] knownRiskByCode)
    {
        float value = 0.5f;
        if (int.TryParse(mcc, out int code) && code >= 0 && code < riskByCode.Length)
            value = knownRiskByCode[code] ? riskByCode[code] : 0.5f;
        return value;
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
    /// Adds a squared distance component and saturates at the provided bound.
    /// </summary>
    /// <param name="value">Current accumulated squared distance.</param>
    /// <param name="diff">Signed feature difference to square.</param>
    /// <param name="bound">Maximum value to return when the addition would reach or exceed the bound.</param>
    /// <returns>The accumulated squared distance or <paramref name="bound"/> when saturated.</returns>
    private static int SaturatingAddSquare(int value, int diff, int bound)
    {
        int square = diff * diff;
        return value >= bound - square ? bound : value + square;
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
    /// Decodes a fine-bucket id back into coarse flags and last-transaction bins.
    /// </summary>
    /// <param name="group">Fine-bucket id produced by <see cref="FineVectorGroup"/>.</param>
    /// <param name="coarse">Decoded four-bit coarse flag group.</param>
    /// <param name="minuteBin">Decoded recency bin.</param>
    /// <param name="kmBin">Decoded distance-from-last bin.</param>
    private static void DecodeFineGroup(int group, out int coarse, out int minuteBin, out int kmBin)
    {
        group /= ExtraBins;
        group /= ExtraBins;
        kmBin = group % FineBins;
        group /= FineBins;
        minuteBin = group % FineBins;
        coarse = group / FineBins;
    }

    /// <summary>
    /// Returns the inclusive low value represented by a fine bin.
    /// </summary>
    /// <param name="bin">Fine bin id.</param>
    /// <param name="scale">Quantization scale used to derive bin boundaries.</param>
    /// <returns>Inclusive lower bound of the bin in quantized units.</returns>
    private static int BinLow(int bin, int scale) => bin * (scale + 1) / FineBins;

    /// <summary>
    /// Returns the inclusive high value represented by a fine bin.
    /// </summary>
    /// <param name="bin">Fine bin id.</param>
    /// <param name="scale">Quantization scale used to derive bin boundaries.</param>
    /// <returns>Inclusive upper bound of the bin in quantized units.</returns>
    private static int BinHigh(int bin, int scale)
    {
        if (bin >= FineBins - 1)
            return scale;

        return ((bin + 1) * (scale + 1) / FineBins) - 1;
    }

    /// <summary>
    /// Computes the minimum distance from a value to a bin interval.
    /// </summary>
    /// <param name="value">Quantized feature value to compare with the bin interval.</param>
    /// <param name="bin">Fine bin id.</param>
    /// <param name="scale">Quantization scale used to derive bin boundaries.</param>
    /// <returns><c>0</c> when the value is inside the bin; otherwise distance to nearest boundary.</returns>
    private static int DistanceToBin(int value, int bin, int scale)
    {
        int low = BinLow(bin, scale);
        if (value < low)
            return low - value;

        int high = BinHigh(bin, scale);
        return value > high ? value - high : 0;
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
