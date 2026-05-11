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
        if (TryParseSinglePassFast(body, out FraudInput fastInput) ||
            TryParseFast(body, out fastInput))
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

    private static bool TryParseSinglePassFast(ReadOnlySpan<byte> body, out FraudInput input)
    {
        input = default;
        int pos = 0;
        if (!TryStartObject(body, ref pos))
            return false;

        int knownMerchantsStart = 0;
        int knownMerchantsLength = 0;
        int merchantStart = 0;
        int merchantLength = 0;
        bool hasMerchant = false;
        bool hasTransaction = false;
        bool hasCustomer = false;
        bool hasMerchantObject = false;
        bool hasTerminal = false;
        bool hasLastTransaction = false;
        bool first = true;

        while (TryReadNextProperty(body, ref pos, ref first, out ReadOnlySpan<byte> property, out bool done))
        {
            if (done)
            {
                input.UnknownMerchant = !hasMerchant ||
                                        !KnownMerchantArrayContains(
                                            body.Slice(knownMerchantsStart, knownMerchantsLength),
                                            body.Slice(merchantStart, merchantLength));
                return hasTransaction && hasCustomer && hasMerchantObject && hasTerminal && hasLastTransaction;
            }

            if (property.SequenceEqual("transaction"u8))
            {
                if (!TryReadTransactionFast(body, ref pos, ref input))
                    return false;
                hasTransaction = true;
            }
            else if (property.SequenceEqual("customer"u8))
            {
                if (!TryReadCustomerFast(body, ref pos, ref input, out knownMerchantsStart, out knownMerchantsLength))
                    return false;
                hasCustomer = true;
            }
            else if (property.SequenceEqual("merchant"u8))
            {
                if (!TryReadMerchantFast(body, ref pos, ref input, out merchantStart, out merchantLength, out hasMerchant))
                    return false;
                hasMerchantObject = true;
            }
            else if (property.SequenceEqual("terminal"u8))
            {
                if (!TryReadTerminalFast(body, ref pos, ref input))
                    return false;
                hasTerminal = true;
            }
            else if (property.SequenceEqual("last_transaction"u8))
            {
                if (!TryReadLastTransactionFast(body, ref pos, ref input))
                    return false;
                hasLastTransaction = true;
            }
            else if (!TrySkipValue(body, ref pos))
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryReadTransactionFast(ReadOnlySpan<byte> source, ref int pos, ref FraudInput input)
    {
        if (!TryStartObject(source, ref pos))
            return false;

        bool hasAmount = false;
        bool hasInstallments = false;
        bool hasRequestedAt = false;
        bool first = true;
        while (TryReadNextProperty(source, ref pos, ref first, out ReadOnlySpan<byte> property, out bool done))
        {
            if (done)
                return hasAmount && hasInstallments && hasRequestedAt;

            if (property.SequenceEqual("amount"u8))
            {
                if (!TryReadDoubleValue(source, ref pos, out input.Amount)) return false;
                hasAmount = true;
            }
            else if (property.SequenceEqual("installments"u8))
            {
                if (!TryReadIntValue(source, ref pos, out input.Installments)) return false;
                hasInstallments = true;
            }
            else if (property.SequenceEqual("requested_at"u8))
            {
                if (!TryReadStringValue(source, ref pos, out ReadOnlySpan<byte> timestamp)) return false;
                FraudVectorizer.ParseIsoUtc(timestamp, out input.Hour, out input.DayOfWeek, out input.RequestedSecondStamp);
                hasRequestedAt = true;
            }
            else if (!TrySkipValue(source, ref pos))
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryReadCustomerFast(ReadOnlySpan<byte> source, ref int pos, ref FraudInput input, out int knownMerchantsStart, out int knownMerchantsLength)
    {
        knownMerchantsStart = 0;
        knownMerchantsLength = 0;
        if (!TryStartObject(source, ref pos))
            return false;

        bool hasAvgAmount = false;
        bool hasTxCount = false;
        bool hasKnownMerchants = false;
        bool first = true;
        while (TryReadNextProperty(source, ref pos, ref first, out ReadOnlySpan<byte> property, out bool done))
        {
            if (done)
                return hasAvgAmount && hasTxCount && hasKnownMerchants;

            if (property.SequenceEqual("avg_amount"u8))
            {
                if (!TryReadDoubleValue(source, ref pos, out input.CustomerAvgAmount)) return false;
                hasAvgAmount = true;
            }
            else if (property.SequenceEqual("tx_count_24h"u8))
            {
                if (!TryReadIntValue(source, ref pos, out input.TxCount24h)) return false;
                hasTxCount = true;
            }
            else if (property.SequenceEqual("known_merchants"u8))
            {
                if (!TryReadArrayBounds(source, ref pos, out knownMerchantsStart, out knownMerchantsLength)) return false;
                hasKnownMerchants = true;
            }
            else if (!TrySkipValue(source, ref pos))
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryReadMerchantFast(ReadOnlySpan<byte> source, ref int pos, ref FraudInput input, out int merchantStart, out int merchantLength, out bool hasMerchant)
    {
        merchantStart = 0;
        merchantLength = 0;
        hasMerchant = false;
        if (!TryStartObject(source, ref pos))
            return false;

        bool hasMcc = false;
        bool hasAvgAmount = false;
        bool first = true;
        while (TryReadNextProperty(source, ref pos, ref first, out ReadOnlySpan<byte> property, out bool done))
        {
            if (done)
                return hasMerchant && hasMcc && hasAvgAmount;

            if (property.SequenceEqual("id"u8))
            {
                if (!TryReadStringBounds(source, ref pos, out merchantStart, out merchantLength)) return false;
                hasMerchant = true;
            }
            else if (property.SequenceEqual("mcc"u8))
            {
                if (!TryReadMccValue(source, ref pos, out input.MccCode)) return false;
                hasMcc = true;
            }
            else if (property.SequenceEqual("avg_amount"u8))
            {
                if (!TryReadDoubleValue(source, ref pos, out input.MerchantAvgAmount)) return false;
                hasAvgAmount = true;
            }
            else if (!TrySkipValue(source, ref pos))
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryReadTerminalFast(ReadOnlySpan<byte> source, ref int pos, ref FraudInput input)
    {
        if (!TryStartObject(source, ref pos))
            return false;

        bool hasIsOnline = false;
        bool hasCardPresent = false;
        bool hasKmFromHome = false;
        bool first = true;
        while (TryReadNextProperty(source, ref pos, ref first, out ReadOnlySpan<byte> property, out bool done))
        {
            if (done)
                return hasIsOnline && hasCardPresent && hasKmFromHome;

            if (property.SequenceEqual("is_online"u8))
            {
                if (!TryReadBoolValue(source, ref pos, out input.IsOnline)) return false;
                hasIsOnline = true;
            }
            else if (property.SequenceEqual("card_present"u8))
            {
                if (!TryReadBoolValue(source, ref pos, out input.CardPresent)) return false;
                hasCardPresent = true;
            }
            else if (property.SequenceEqual("km_from_home"u8))
            {
                if (!TryReadDoubleValue(source, ref pos, out input.KmFromHome)) return false;
                hasKmFromHome = true;
            }
            else if (!TrySkipValue(source, ref pos))
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryReadLastTransactionFast(ReadOnlySpan<byte> source, ref int pos, ref FraudInput input)
    {
        pos = SkipWhitespace(source, pos);
        if (pos + 4 <= source.Length && source.Slice(pos, 4).SequenceEqual("null"u8))
        {
            pos += 4;
            return true;
        }

        if (!TryStartObject(source, ref pos))
            return false;

        input.HasLastTransaction = true;
        bool hasTimestamp = false;
        bool hasKmFromCurrent = false;
        bool first = true;
        while (TryReadNextProperty(source, ref pos, ref first, out ReadOnlySpan<byte> property, out bool done))
        {
            if (done)
                return hasTimestamp && hasKmFromCurrent;

            if (property.SequenceEqual("timestamp"u8))
            {
                if (!TryReadStringValue(source, ref pos, out ReadOnlySpan<byte> timestamp)) return false;
                FraudVectorizer.ParseIsoUtc(timestamp, out _, out _, out input.LastSecondStamp);
                hasTimestamp = true;
            }
            else if (property.SequenceEqual("km_from_current"u8))
            {
                if (!TryReadDoubleValue(source, ref pos, out input.KmFromCurrent)) return false;
                hasKmFromCurrent = true;
            }
            else if (!TrySkipValue(source, ref pos))
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryReadArrayBounds(ReadOnlySpan<byte> source, ref int pos, out int start, out int length)
    {
        start = 0;
        length = 0;
        pos = SkipWhitespace(source, pos);
        if (pos >= source.Length || source[pos] != (byte)'[')
            return false;

        start = pos;
        if (!TrySkipComposite(source, ref pos, (byte)'[', (byte)']'))
            return false;

        length = pos - start;
        return true;
    }

    private static bool TryReadStringBounds(ReadOnlySpan<byte> source, ref int pos, out int start, out int length)
    {
        start = 0;
        length = 0;
        if (!TryReadStringValue(source, ref pos, out ReadOnlySpan<byte> value))
            return false;

        start = (int)Unsafe.ByteOffset(
            ref MemoryMarshal.GetReference(source),
            ref MemoryMarshal.GetReference(value));
        length = value.Length;
        return true;
    }

    private static bool TryStartObject(ReadOnlySpan<byte> source, ref int pos)
    {
        pos = SkipWhitespace(source, pos);
        if (pos >= source.Length || source[pos] != (byte)'{')
            return false;

        pos++;
        return true;
    }

    private static bool TryReadNextProperty(ReadOnlySpan<byte> source, ref int pos, ref bool first, out ReadOnlySpan<byte> property, out bool done)
    {
        property = default;
        done = false;
        pos = SkipWhitespace(source, pos);
        if (!first)
        {
            if (pos < source.Length && source[pos] == (byte)',')
            {
                pos++;
                pos = SkipWhitespace(source, pos);
            }
            else if (pos < source.Length && source[pos] == (byte)'}')
            {
                pos++;
                done = true;
                return true;
            }
            else
            {
                return false;
            }
        }
        else if (pos < source.Length && source[pos] == (byte)'}')
        {
            pos++;
            done = true;
            return true;
        }

        first = false;
        if (!TryReadStringValue(source, ref pos, out property))
            return false;

        pos = SkipWhitespace(source, pos);
        if (pos >= source.Length || source[pos] != (byte)':')
            return false;

        pos++;
        return true;
    }

    private static bool TryReadStringValue(ReadOnlySpan<byte> source, ref int pos, out ReadOnlySpan<byte> value)
    {
        value = default;
        pos = SkipWhitespace(source, pos);
        if (pos >= source.Length || source[pos] != (byte)'"')
            return false;

        int start = ++pos;
        while (pos < source.Length)
        {
            byte current = source[pos];
            if (current == (byte)'\\')
                return false;
            if (current == (byte)'"')
            {
                value = source.Slice(start, pos - start);
                pos++;
                return true;
            }

            pos++;
        }

        return false;
    }

    private static bool TryReadDoubleValue(ReadOnlySpan<byte> source, ref int pos, out double value)
    {
        pos = SkipWhitespace(source, pos);
        if (!Utf8Parser.TryParse(source[pos..], out value, out int consumed) || consumed <= 0)
            return false;

        pos += consumed;
        return true;
    }

    private static bool TryReadIntValue(ReadOnlySpan<byte> source, ref int pos, out int value)
    {
        pos = SkipWhitespace(source, pos);
        if (!Utf8Parser.TryParse(source[pos..], out value, out int consumed) || consumed <= 0)
            return false;

        pos += consumed;
        return true;
    }

    private static bool TryReadBoolValue(ReadOnlySpan<byte> source, ref int pos, out bool value)
    {
        value = false;
        pos = SkipWhitespace(source, pos);
        if (pos + 4 <= source.Length && source.Slice(pos, 4).SequenceEqual("true"u8))
        {
            value = true;
            pos += 4;
            return true;
        }

        if (pos + 5 <= source.Length && source.Slice(pos, 5).SequenceEqual("false"u8))
        {
            pos += 5;
            return true;
        }

        return false;
    }

    private static bool TryReadMccValue(ReadOnlySpan<byte> source, ref int pos, out int code)
    {
        pos = SkipWhitespace(source, pos);
        if (pos < source.Length && source[pos] == (byte)'"')
        {
            if (!TryReadStringValue(source, ref pos, out ReadOnlySpan<byte> text))
            {
                code = -1;
                return false;
            }

            return TryParseDigits(text, out code);
        }

        return TryReadIntValue(source, ref pos, out code);
    }

    private static bool TrySkipValue(ReadOnlySpan<byte> source, ref int pos)
    {
        pos = SkipWhitespace(source, pos);
        if (pos >= source.Length)
            return false;

        byte current = source[pos];
        if (current == (byte)'"')
            return TryReadStringValue(source, ref pos, out _);
        if (current == (byte)'{')
            return TrySkipComposite(source, ref pos, (byte)'{', (byte)'}');
        if (current == (byte)'[')
            return TrySkipComposite(source, ref pos, (byte)'[', (byte)']');
        if (pos + 4 <= source.Length && source.Slice(pos, 4).SequenceEqual("true"u8)) { pos += 4; return true; }
        if (pos + 5 <= source.Length && source.Slice(pos, 5).SequenceEqual("false"u8)) { pos += 5; return true; }
        if (pos + 4 <= source.Length && source.Slice(pos, 4).SequenceEqual("null"u8)) { pos += 4; return true; }

        while (pos < source.Length)
        {
            current = source[pos];
            if (current == (byte)',' || current == (byte)'}' || current == (byte)']' || current == (byte)' ' || current == (byte)'\n' || current == (byte)'\r' || current == (byte)'\t')
                return true;
            pos++;
        }

        return true;
    }

    private static bool TrySkipComposite(ReadOnlySpan<byte> source, ref int pos, byte open, byte close)
    {
        int depth = 0;
        while (pos < source.Length)
        {
            byte current = source[pos++];
            if (current == (byte)'"')
            {
                pos--;
                if (!TryReadStringValue(source, ref pos, out _))
                    return false;
                continue;
            }

            if (current == open)
                depth++;
            else if (current == close && --depth == 0)
                return true;
        }

        return false;
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

    private static bool KnownMerchantArrayContains(ReadOnlySpan<byte> knownMerchants, ReadOnlySpan<byte> merchantId)
    {
        int pos = 0;
        pos = SkipWhitespace(knownMerchants, pos);
        if (pos >= knownMerchants.Length || knownMerchants[pos++] != (byte)'[')
            return false;

        bool first = true;
        while (true)
        {
            pos = SkipWhitespace(knownMerchants, pos);
            if (!first)
            {
                if (pos < knownMerchants.Length && knownMerchants[pos] == (byte)',')
                {
                    pos++;
                    pos = SkipWhitespace(knownMerchants, pos);
                }
                else if (pos < knownMerchants.Length && knownMerchants[pos] == (byte)']')
                {
                    return false;
                }
                else
                {
                    return false;
                }
            }
            else if (pos < knownMerchants.Length && knownMerchants[pos] == (byte)']')
            {
                return false;
            }

            first = false;
            if (!TryReadStringValue(knownMerchants, ref pos, out ReadOnlySpan<byte> knownMerchant))
                return false;
            if (knownMerchant.SequenceEqual(merchantId))
                return true;
        }
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
