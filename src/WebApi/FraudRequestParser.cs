/// <summary>
/// Allocation-conscious JSON parser for the Rinha fraud-score request body.
/// </summary>
/// <remarks>
/// It reads only fields used by vectorization and skips unknown properties. The
/// parser uses <see cref="Utf8JsonReader"/> over the socket buffer to avoid
/// ASP.NET model binding and general-purpose object materialization costs.
/// </remarks>
internal static class FraudRequestParser
{
    /// <summary>
    /// Parses a UTF-8 fraud-score request body into the compact scoring input.
    /// </summary>
    /// <param name="body">Complete UTF-8 JSON request body.</param>
    /// <returns>A populated <see cref="FraudInput"/> used by <see cref="FraudScorer"/>.</returns>
    /// <exception cref="JsonException">Thrown when the request shape or token type is invalid.</exception>
    public static FraudInput Parse(ReadOnlySpan<byte> body)
    {
        if (TryParseFast(body, out FraudInput fastInput))
            return fastInput;

        var reader = new Utf8JsonReader(body);
        var input = new FraudInput();
        ulong merchantHash = 0;
        int merchantLength = 0;
        bool hasMerchant = false;
        Span<ulong> knownHashes = stackalloc ulong[32];
        Span<int> knownLengths = stackalloc int[32];
        int knownCount = 0;
        List<(ulong Hash, int Length)>? knownExtra = null;

        RequireRead(ref reader);
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException();

        while (RequireRead(ref reader) && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException();

            if (reader.ValueTextEquals("transaction"u8))
            {
                RequireRead(ref reader);
                ReadTransaction(ref reader, ref input);
            }
            else if (reader.ValueTextEquals("customer"u8))
            {
                RequireRead(ref reader);
                ReadCustomer(ref reader, ref input, knownHashes, knownLengths, ref knownCount, ref knownExtra);
            }
            else if (reader.ValueTextEquals("merchant"u8))
            {
                RequireRead(ref reader);
                ReadMerchant(ref reader, ref input, out merchantHash, out merchantLength, out hasMerchant);
            }
            else if (reader.ValueTextEquals("terminal"u8))
            {
                RequireRead(ref reader);
                ReadTerminal(ref reader, ref input);
            }
            else if (reader.ValueTextEquals("last_transaction"u8))
            {
                RequireRead(ref reader);
                ReadLastTransaction(ref reader, ref input);
            }
            else
            {
                RequireRead(ref reader);
                reader.Skip();
            }
        }

        input.UnknownMerchant = !MerchantIsKnown(merchantHash, merchantLength, hasMerchant, knownHashes, knownLengths, knownCount, knownExtra);
        return input;
    }

    private static bool TryParseFast(ReadOnlySpan<byte> body, out FraudInput input)
    {
        input = default;

        if (!TryGetObject(body, "transaction"u8, out ReadOnlySpan<byte> transaction) ||
            !TryReadDouble(transaction, "amount"u8, out input.Amount) ||
            !TryReadInt(transaction, "installments"u8, out input.Installments) ||
            !TryReadString(transaction, "requested_at"u8, out ReadOnlySpan<byte> requestedAt))
            return false;

        FraudVectorizer.ParseIsoUtc(requestedAt, out input.Hour, out input.DayOfWeek, out input.RequestedSecondStamp);

        if (!TryGetObject(body, "customer"u8, out ReadOnlySpan<byte> customer) ||
            !TryReadDouble(customer, "avg_amount"u8, out input.CustomerAvgAmount) ||
            !TryReadInt(customer, "tx_count_24h"u8, out input.TxCount24h))
            return false;

        if (!TryGetObject(body, "merchant"u8, out ReadOnlySpan<byte> merchant) ||
            !TryReadString(merchant, "id"u8, out ReadOnlySpan<byte> merchantId) ||
            !TryReadMcc(merchant, out input.MccCode) ||
            !TryReadDouble(merchant, "avg_amount"u8, out input.MerchantAvgAmount))
            return false;

        if (!TryGetObject(body, "terminal"u8, out ReadOnlySpan<byte> terminal) ||
            !TryReadBool(terminal, "is_online"u8, out input.IsOnline) ||
            !TryReadBool(terminal, "card_present"u8, out input.CardPresent) ||
            !TryReadDouble(terminal, "km_from_home"u8, out input.KmFromHome))
            return false;

        if (!TryReadKnownMerchant(customer, merchantId, out bool knownMerchant))
            return false;
        input.UnknownMerchant = !knownMerchant;

        if (!TryGetNullableObject(body, "last_transaction"u8, out ReadOnlySpan<byte> lastTransaction, out bool hasLastTransaction))
            return false;

        if (hasLastTransaction)
        {
            if (!TryReadString(lastTransaction, "timestamp"u8, out ReadOnlySpan<byte> lastTimestamp) ||
                !TryReadDouble(lastTransaction, "km_from_current"u8, out input.KmFromCurrent))
                return false;

            FraudVectorizer.ParseIsoUtc(lastTimestamp, out _, out _, out input.LastSecondStamp);
            input.HasLastTransaction = true;
        }

        return true;
    }

    private static bool TryGetObject(ReadOnlySpan<byte> body, ReadOnlySpan<byte> propertyName, out ReadOnlySpan<byte> value) =>
        TryGetNullableObject(body, propertyName, out value, out bool hasValue) && hasValue;

    private static bool TryGetNullableObject(ReadOnlySpan<byte> body, ReadOnlySpan<byte> propertyName, out ReadOnlySpan<byte> value, out bool hasValue)
    {
        value = default;
        hasValue = false;
        if (!TryFindValue(body, propertyName, out int start))
            return false;

        if (start + 4 <= body.Length && body.Slice(start, 4).SequenceEqual("null"u8))
            return true;

        if (start >= body.Length || body[start] != (byte)'{')
            return false;

        int end = FindMatchingBrace(body, start);
        if (end < 0)
            return false;

        value = body.Slice(start + 1, end - start - 1);
        hasValue = true;
        return true;
    }

    private static bool TryReadDouble(ReadOnlySpan<byte> source, ReadOnlySpan<byte> propertyName, out double value)
    {
        value = 0;
        return TryFindValue(source, propertyName, out int start) &&
               Utf8Parser.TryParse(source[start..], out value, out _);
    }

    private static bool TryReadInt(ReadOnlySpan<byte> source, ReadOnlySpan<byte> propertyName, out int value)
    {
        value = 0;
        return TryFindValue(source, propertyName, out int start) &&
               Utf8Parser.TryParse(source[start..], out value, out _);
    }

    private static bool TryReadBool(ReadOnlySpan<byte> source, ReadOnlySpan<byte> propertyName, out bool value)
    {
        value = false;
        if (!TryFindValue(source, propertyName, out int start))
            return false;

        if (start + 4 <= source.Length && source.Slice(start, 4).SequenceEqual("true"u8))
        {
            value = true;
            return true;
        }

        if (start + 5 <= source.Length && source.Slice(start, 5).SequenceEqual("false"u8))
            return true;

        return false;
    }

    private static bool TryReadString(ReadOnlySpan<byte> source, ReadOnlySpan<byte> propertyName, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (!TryFindValue(source, propertyName, out int start) || start >= source.Length || source[start] != (byte)'\"')
            return false;

        int contentStart = start + 1;
        for (int i = contentStart; i < source.Length; i++)
        {
            byte current = source[i];
            if (current == (byte)'\\')
                return false;
            if (current == (byte)'\"')
            {
                value = source.Slice(contentStart, i - contentStart);
                return true;
            }
        }

        return false;
    }

    private static bool TryReadMcc(ReadOnlySpan<byte> merchant, out int code)
    {
        code = -1;
        if (!TryFindValue(merchant, "mcc"u8, out int start))
            return false;

        if (start < merchant.Length && merchant[start] == (byte)'\"')
        {
            int contentStart = start + 1;
            int contentEnd = contentStart;
            while (contentEnd < merchant.Length && merchant[contentEnd] != (byte)'\"')
            {
                if (merchant[contentEnd] == (byte)'\\')
                    return false;
                contentEnd++;
            }

            return TryParseDigits(merchant.Slice(contentStart, contentEnd - contentStart), out code);
        }

        return Utf8Parser.TryParse(merchant[start..], out code, out _);
    }

    private static bool TryReadKnownMerchant(ReadOnlySpan<byte> customer, ReadOnlySpan<byte> merchantId, out bool known)
    {
        known = false;
        if (!TryFindValue(customer, "known_merchants"u8, out int start) || start >= customer.Length || customer[start] != (byte)'[')
            return false;

        int pos = start + 1;
        while (pos < customer.Length)
        {
            pos = SkipWhitespace(customer, pos);
            if (pos >= customer.Length)
                return false;
            if (customer[pos] == (byte)']')
                return true;
            if (customer[pos] != (byte)'\"')
                return false;

            int contentStart = ++pos;
            while (pos < customer.Length && customer[pos] != (byte)'\"')
            {
                if (customer[pos] == (byte)'\\')
                    return false;
                pos++;
            }

            if (pos >= customer.Length)
                return false;

            if (customer.Slice(contentStart, pos - contentStart).SequenceEqual(merchantId))
                known = true;

            pos = SkipWhitespace(customer, pos + 1);
            if (pos < customer.Length && customer[pos] == (byte)',')
                pos++;
        }

        return false;
    }

    private static bool TryFindValue(ReadOnlySpan<byte> source, ReadOnlySpan<byte> propertyName, out int valueStart)
    {
        valueStart = -1;
        Span<byte> pattern = stackalloc byte[propertyName.Length + 2];
        pattern[0] = (byte)'\"';
        propertyName.CopyTo(pattern[1..]);
        pattern[^1] = (byte)'\"';

        int searchStart = 0;
        while (searchStart < source.Length)
        {
            int relative = source[searchStart..].IndexOf(pattern);
            if (relative < 0)
                return false;

            int pos = searchStart + relative + pattern.Length;
            pos = SkipWhitespace(source, pos);
            if (pos < source.Length && source[pos] == (byte)':')
            {
                valueStart = SkipWhitespace(source, pos + 1);
                return valueStart < source.Length;
            }

            searchStart += relative + 1;
        }

        return false;
    }

    private static int FindMatchingBrace(ReadOnlySpan<byte> source, int start)
    {
        int depth = 0;
        bool inString = false;
        for (int i = start; i < source.Length; i++)
        {
            byte current = source[i];
            if (inString)
            {
                if (current == (byte)'\\')
                    return -1;
                if (current == (byte)'\"')
                    inString = false;
                continue;
            }

            if (current == (byte)'\"')
                inString = true;
            else if (current == (byte)'{')
                depth++;
            else if (current == (byte)'}' && --depth == 0)
                return i;
        }

        return -1;
    }

    private static int SkipWhitespace(ReadOnlySpan<byte> source, int pos)
    {
        while (pos < source.Length)
        {
            byte value = source[pos];
            if (value != (byte)' ' && value != (byte)'\n' && value != (byte)'\r' && value != (byte)'\t')
                break;
            pos++;
        }

        return pos;
    }

    private static bool TryParseDigits(ReadOnlySpan<byte> value, out int parsed)
    {
        parsed = 0;
        for (int i = 0; i < value.Length; i++)
        {
            byte digit = (byte)(value[i] - (byte)'0');
            if (digit > 9)
            {
                parsed = -1;
                return true;
            }

            parsed = parsed * 10 + digit;
        }

        return true;
    }

    /// <summary>
    /// Reads the <c>transaction</c> object fields required for amount, installments, and time features.
    /// </summary>
    /// <param name="reader">JSON reader positioned on the transaction object start token.</param>
    /// <param name="input">Mutable scoring input receiving parsed transaction fields.</param>
    /// <exception cref="JsonException">Thrown when the object shape is invalid.</exception>
    private static void ReadTransaction(ref Utf8JsonReader reader, ref FraudInput input)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException();

        while (RequireRead(ref reader) && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException();

            if (reader.ValueTextEquals("amount"u8))
            {
                RequireRead(ref reader);
                input.Amount = reader.GetDouble();
            }
            else if (reader.ValueTextEquals("installments"u8))
            {
                RequireRead(ref reader);
                input.Installments = reader.GetInt32();
            }
            else if (reader.ValueTextEquals("requested_at"u8))
            {
                RequireRead(ref reader);
                ReadIsoUtc(ref reader, out input.Hour, out input.DayOfWeek, out input.RequestedSecondStamp);
            }
            else
            {
                RequireRead(ref reader);
                reader.Skip();
            }
        }
    }

    /// <summary>
    /// Reads the <c>customer</c> object fields used for normalization and known-merchant detection.
    /// </summary>
    /// <param name="reader">JSON reader positioned on the customer object start token.</param>
    /// <param name="input">Mutable scoring input receiving aggregate customer fields.</param>
    /// <param name="knownHashes">Fixed stack storage for known merchant hashes.</param>
    /// <param name="knownLengths">Fixed stack storage for known merchant lengths.</param>
    /// <param name="knownCount">Number of known merchants stored in the fixed spans.</param>
    /// <param name="knownExtra">Spill list allocated only when fixed storage is exceeded.</param>
    /// <exception cref="JsonException">Thrown when the object or known-merchants array shape is invalid.</exception>
    private static void ReadCustomer(
        ref Utf8JsonReader reader,
        ref FraudInput input,
        scoped Span<ulong> knownHashes,
        scoped Span<int> knownLengths,
        ref int knownCount,
        ref List<(ulong Hash, int Length)>? knownExtra)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException();

        while (RequireRead(ref reader) && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException();

            if (reader.ValueTextEquals("avg_amount"u8))
            {
                RequireRead(ref reader);
                input.CustomerAvgAmount = reader.GetDouble();
            }
            else if (reader.ValueTextEquals("tx_count_24h"u8))
            {
                RequireRead(ref reader);
                input.TxCount24h = reader.GetInt32();
            }
            else if (reader.ValueTextEquals("known_merchants"u8))
            {
                RequireRead(ref reader);
                ReadKnownMerchants(ref reader, knownHashes, knownLengths, ref knownCount, ref knownExtra);
            }
            else
            {
                RequireRead(ref reader);
                reader.Skip();
            }
        }
    }

    /// <summary>
    /// Reads merchant identity, MCC, and average amount from the <c>merchant</c> object.
    /// </summary>
    /// <param name="reader">JSON reader positioned on the merchant object start token.</param>
    /// <param name="input">Mutable scoring input receiving MCC and average amount fields.</param>
    /// <param name="merchantHash">Merchant id hash used after parsing to compute <see cref="FraudInput.UnknownMerchant"/>.</param>
    /// <param name="merchantLength">Merchant id byte length used with the hash.</param>
    /// <param name="hasMerchant">Whether merchant id was present.</param>
    /// <exception cref="JsonException">Thrown when the object shape is invalid.</exception>
    private static void ReadMerchant(ref Utf8JsonReader reader, ref FraudInput input, out ulong merchantHash, out int merchantLength, out bool hasMerchant)
    {
        merchantHash = 0;
        merchantLength = 0;
        hasMerchant = false;

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException();

        while (RequireRead(ref reader) && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException();

            if (reader.ValueTextEquals("id"u8))
            {
                RequireRead(ref reader);
                ReadStringHash(ref reader, out merchantHash, out merchantLength);
                hasMerchant = true;
            }
            else if (reader.ValueTextEquals("mcc"u8))
            {
                RequireRead(ref reader);
                input.MccCode = ReadMccCode(ref reader);
            }
            else if (reader.ValueTextEquals("avg_amount"u8))
            {
                RequireRead(ref reader);
                input.MerchantAvgAmount = reader.GetDouble();
            }
            else
            {
                RequireRead(ref reader);
                reader.Skip();
            }
        }
    }

    /// <summary>
    /// Reads terminal flags and home distance from the <c>terminal</c> object.
    /// </summary>
    /// <param name="reader">JSON reader positioned on the terminal object start token.</param>
    /// <param name="input">Mutable scoring input receiving terminal features.</param>
    /// <exception cref="JsonException">Thrown when the object shape is invalid.</exception>
    private static void ReadTerminal(ref Utf8JsonReader reader, ref FraudInput input)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException();

        while (RequireRead(ref reader) && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException();

            if (reader.ValueTextEquals("is_online"u8))
            {
                RequireRead(ref reader);
                input.IsOnline = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals("card_present"u8))
            {
                RequireRead(ref reader);
                input.CardPresent = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals("km_from_home"u8))
            {
                RequireRead(ref reader);
                input.KmFromHome = reader.GetDouble();
            }
            else
            {
                RequireRead(ref reader);
                reader.Skip();
            }
        }
    }

    /// <summary>
    /// Reads nullable <c>last_transaction</c> data and marks whether recency features exist.
    /// </summary>
    /// <param name="reader">JSON reader positioned on null or the last-transaction object start token.</param>
    /// <param name="input">Mutable scoring input receiving last-transaction features.</param>
    /// <exception cref="JsonException">Thrown when a non-null value is not an object.</exception>
    private static void ReadLastTransaction(ref Utf8JsonReader reader, ref FraudInput input)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return;

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException();

        input.HasLastTransaction = true;

        while (RequireRead(ref reader) && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException();

            if (reader.ValueTextEquals("timestamp"u8))
            {
                RequireRead(ref reader);
                ReadIsoUtc(ref reader, out _, out _, out input.LastSecondStamp);
            }
            else if (reader.ValueTextEquals("km_from_current"u8))
            {
                RequireRead(ref reader);
                input.KmFromCurrent = reader.GetDouble();
            }
            else
            {
                RequireRead(ref reader);
                reader.Skip();
            }
        }
    }

    /// <summary>
    /// Reads known merchant ids into four scalar slots plus an optional spill list.
    /// </summary>
    /// <param name="reader">JSON reader positioned on the known-merchants array start token.</param>
    /// <param name="knownHashes">Fixed stack storage for known merchant hashes.</param>
    /// <param name="knownLengths">Fixed stack storage for known merchant lengths.</param>
    /// <param name="knownCount">Number of known merchants stored in the fixed spans.</param>
    /// <param name="knownExtra">Spill list for merchants after fixed storage fills.</param>
    /// <exception cref="JsonException">Thrown when the token is not an array.</exception>
    private static void ReadKnownMerchants(
        ref Utf8JsonReader reader,
        scoped Span<ulong> knownHashes,
        scoped Span<int> knownLengths,
        ref int knownCount,
        ref List<(ulong Hash, int Length)>? knownExtra)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException();

        int count = 0;
        while (RequireRead(ref reader) && reader.TokenType != JsonTokenType.EndArray)
        {
            ReadStringHash(ref reader, out ulong hash, out int length);
            if (count < knownHashes.Length)
            {
                knownHashes[count] = hash;
                knownLengths[count] = length;
                knownCount = count + 1;
            }
            else
            {
                knownExtra ??= new List<(ulong Hash, int Length)>(4);
                knownExtra.Add((hash, length));
            }

            count++;
        }
    }

    /// <summary>
    /// Checks whether a merchant id matches any known merchant slot.
    /// </summary>
    /// <param name="merchantHash">Merchant id hash from the request merchant object.</param>
    /// <param name="merchantLength">Merchant id byte length.</param>
    /// <param name="hasMerchant">Whether merchant id was present.</param>
    /// <param name="knownHashes">Fixed stack storage for known merchant hashes.</param>
    /// <param name="knownLengths">Fixed stack storage for known merchant lengths.</param>
    /// <param name="knownCount">Number of known merchants stored in the fixed spans.</param>
    /// <param name="knownExtra">Optional spill list for additional known merchants.</param>
    /// <returns><see langword="true"/> when the merchant hash and length are present in any slot.</returns>
    private static bool MerchantIsKnown(
        ulong merchantHash,
        int merchantLength,
        bool hasMerchant,
        scoped ReadOnlySpan<ulong> knownHashes,
        scoped ReadOnlySpan<int> knownLengths,
        int knownCount,
        List<(ulong Hash, int Length)>? knownExtra)
    {
        if (!hasMerchant)
            return false;

        for (int i = 0; i < knownCount; i++)
        {
            if (merchantHash == knownHashes[i] && merchantLength == knownLengths[i])
                return true;
        }

        if (knownExtra is null)
            return false;

        for (int i = 0; i < knownExtra.Count; i++)
        {
            (ulong hash, int length) = knownExtra[i];
            if (merchantHash == hash && merchantLength == length)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Reads a JSON string token as an FNV-1a hash plus byte length, avoiding string allocation on contiguous spans.
    /// </summary>
    /// <param name="reader">Reader positioned on a string token.</param>
    /// <param name="hash">Computed FNV-1a hash.</param>
    /// <param name="length">UTF-8 byte length.</param>
    /// <exception cref="JsonException">Thrown when the token is not a string.</exception>
    private static void ReadStringHash(ref Utf8JsonReader reader, out ulong hash, out int length)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException();

        ReadOnlySpan<byte> span = reader.HasValueSequence ? Encoding.UTF8.GetBytes(reader.GetString()!) : reader.ValueSpan;
        hash = Fnv1A(span);
        length = span.Length;
    }

    /// <summary>
    /// Computes FNV-1a 64-bit hash for merchant identity comparisons.
    /// </summary>
    /// <param name="value">UTF-8 string bytes.</param>
    /// <returns>FNV-1a 64-bit hash.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Fnv1A(ReadOnlySpan<byte> value)
    {
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < value.Length; i++)
        {
            hash ^= value[i];
            hash *= 1099511628211UL;
        }

        return hash;
    }

    /// <summary>
    /// Reads an ISO UTC JSON string into hour, Monday-based weekday, and absolute second stamp.
    /// </summary>
    /// <param name="reader">JSON reader positioned on the timestamp string token.</param>
    /// <param name="hour">Parsed UTC hour from 0 through 23.</param>
    /// <param name="dayOfWeek">Parsed Monday-based weekday from 0 through 6.</param>
    /// <param name="secondStamp">Parsed absolute second stamp used for fast elapsed-time subtraction.</param>
    /// <exception cref="JsonException">Thrown when the token is not a string.</exception>
    private static void ReadIsoUtc(ref Utf8JsonReader reader, out int hour, out int dayOfWeek, out int secondStamp)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException();

        if (reader.HasValueSequence)
        {
            FraudVectorizer.ParseIsoUtc(reader.GetString()!, out hour, out dayOfWeek, out secondStamp);
            return;
        }

        FraudVectorizer.ParseIsoUtc(reader.ValueSpan, out hour, out dayOfWeek, out secondStamp);
    }

    /// <summary>
    /// Reads an MCC code from numeric or string JSON tokens.
    /// </summary>
    /// <param name="reader">JSON reader positioned on the MCC value token.</param>
    /// <returns>The parsed numeric MCC, or <c>-1</c> for non-digit string values.</returns>
    /// <exception cref="JsonException">Thrown when the token is neither number nor string.</exception>
    private static int ReadMccCode(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return reader.GetInt32();

        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException();

        ReadOnlySpan<byte> span = reader.HasValueSequence ? Encoding.UTF8.GetBytes(reader.GetString()!) : reader.ValueSpan;
        int code = 0;
        for (int i = 0; i < span.Length; i++)
        {
            byte digit = (byte)(span[i] - (byte)'0');
            if (digit > 9)
                return -1;
            code = code * 10 + digit;
        }

        return code;
    }

    /// <summary>
    /// Advances the JSON reader or throws when the payload ends unexpectedly.
    /// </summary>
    /// <param name="reader">Reader to advance by one token.</param>
    /// <returns>Always <see langword="true"/> when a token was read; useful in loop conditions.</returns>
    /// <exception cref="JsonException">Thrown when no further JSON token exists.</exception>
    private static bool RequireRead(ref Utf8JsonReader reader)
    {
        if (!reader.Read())
            throw new JsonException();
        return true;
    }
}
