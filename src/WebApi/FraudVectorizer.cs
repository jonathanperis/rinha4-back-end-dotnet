public static class FraudVectorizer
{
    // Fine bucket layout: coarse risk flags, last-transaction bins, amount bin,
    // and home-distance bin. This gives an O(1) lookup key for default scoring.
    public const int FineBins = 32;
    public const int ExtraBins = 16;
    public const int FineGroupCount = 16 * FineBins * FineBins * ExtraBins * ExtraBins;

    public static float NormalizeDayOfWeek(DateTime requestedAt) =>
        (((int)requestedAt.DayOfWeek + 6) % 7) / 6.0f;

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

    public static int IsoUtcMinuteStamp(string value)
    {
        ParseIsoUtc(value, out _, out _, out int minuteStamp);
        return minuteStamp;
    }

    public static int UnknownMerchant(string merchantId, string[] knownMerchants)
    {
        for (int i = 0; i < knownMerchants.Length; i++)
        {
            if (knownMerchants[i] == merchantId)
                return 0;
        }

        return 1;
    }

    public static int VectorGroup(int minutesSinceLast, int isOnline, int cardPresent, int unknownMerchant)
    {
        // Coarse group packs four binary dimensions into one nibble.
        int group = minutesSinceLast >= 0 ? 1 : 0;
        if (isOnline > 0) group |= 2;
        if (cardPresent > 0) group |= 4;
        if (unknownMerchant > 0) group |= 8;
        return group;
    }

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

    public static float LookupMccRisk(string mcc, float[] riskByCode, bool[] knownRiskByCode)
    {
        float value = 0.5f;
        if (int.TryParse(mcc, out int code) && code >= 0 && code < riskByCode.Length)
            value = knownRiskByCode[code] ? riskByCode[code] : 0.5f;
        return value;
    }

    private static int Parse2(string value, int index) =>
        (value[index] - '0') * 10 + value[index + 1] - '0';

    private static int Parse4(string value, int index) =>
        (value[index] - '0') * 1000 + (value[index + 1] - '0') * 100 + (value[index + 2] - '0') * 10 + value[index + 3] - '0';

    private static int Parse2(ReadOnlySpan<byte> value, int index) =>
        (value[index] - (byte)'0') * 10 + value[index + 1] - (byte)'0';

    private static int Parse4(ReadOnlySpan<byte> value, int index) =>
        (value[index] - (byte)'0') * 1000 + (value[index + 1] - (byte)'0') * 100 + (value[index + 2] - (byte)'0') * 10 + value[index + 3] - (byte)'0';

    private static int Mod7(int value)
    {
        int result = value % 7;
        return result < 0 ? result + 7 : result;
    }

    private static int SaturatingAddSquare(int value, int diff, int bound)
    {
        int square = diff * diff;
        return value >= bound - square ? bound : value + square;
    }

    private static int ContinuousBin(int value, int scale)
    {
        if (value <= 0) return 0;
        if (value >= scale) return FineBins - 1;
        return value * FineBins / (scale + 1);
    }

    private static int ExtraBin(int value, int scale)
    {
        if (value <= 0) return 0;
        if (value >= scale) return ExtraBins - 1;
        return value * ExtraBins / (scale + 1);
    }

    private static void DecodeFineGroup(int group, out int coarse, out int minuteBin, out int kmBin)
    {
        group /= ExtraBins;
        group /= ExtraBins;
        kmBin = group % FineBins;
        group /= FineBins;
        minuteBin = group % FineBins;
        coarse = group / FineBins;
    }

    private static int BinLow(int bin, int scale) => bin * (scale + 1) / FineBins;

    private static int BinHigh(int bin, int scale)
    {
        if (bin >= FineBins - 1)
            return scale;

        return ((bin + 1) * (scale + 1) / FineBins) - 1;
    }

    private static int DistanceToBin(int value, int bin, int scale)
    {
        int low = BinLow(bin, scale);
        if (value < low)
            return low - value;

        int high = BinHigh(bin, scale);
        return value > high ? value - high : 0;
    }

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
