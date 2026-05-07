using System.Text;
using System.Text.Json;

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
        var reader = new Utf8JsonReader(body);
        var input = new FraudInput();
        string? merchantId = null;
        string? known0 = null;
        string? known1 = null;
        string? known2 = null;
        string? known3 = null;
        List<string>? knownExtra = null;

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
                ReadCustomer(ref reader, ref input, ref known0, ref known1, ref known2, ref known3, ref knownExtra);
            }
            else if (reader.ValueTextEquals("merchant"u8))
            {
                RequireRead(ref reader);
                ReadMerchant(ref reader, ref input, ref merchantId);
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

        input.UnknownMerchant = !MerchantIsKnown(merchantId, known0, known1, known2, known3, knownExtra);
        return input;
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
                input.Amount = reader.GetSingle();
            }
            else if (reader.ValueTextEquals("installments"u8))
            {
                RequireRead(ref reader);
                input.Installments = reader.GetInt32();
            }
            else if (reader.ValueTextEquals("requested_at"u8))
            {
                RequireRead(ref reader);
                ReadIsoUtc(ref reader, out input.Hour, out input.DayOfWeek, out input.RequestedMinuteStamp);
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
    /// <param name="known0">First known merchant slot, avoiding list allocation for small arrays.</param>
    /// <param name="known1">Second known merchant slot, avoiding list allocation for small arrays.</param>
    /// <param name="known2">Third known merchant slot, avoiding list allocation for small arrays.</param>
    /// <param name="known3">Fourth known merchant slot, avoiding list allocation for small arrays.</param>
    /// <param name="knownExtra">Spill list allocated only when more than four known merchants are present.</param>
    /// <exception cref="JsonException">Thrown when the object or known-merchants array shape is invalid.</exception>
    private static void ReadCustomer(
        ref Utf8JsonReader reader,
        ref FraudInput input,
        ref string? known0,
        ref string? known1,
        ref string? known2,
        ref string? known3,
        ref List<string>? knownExtra)
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
                input.CustomerAvgAmount = reader.GetSingle();
            }
            else if (reader.ValueTextEquals("tx_count_24h"u8))
            {
                RequireRead(ref reader);
                input.TxCount24h = reader.GetInt32();
            }
            else if (reader.ValueTextEquals("known_merchants"u8))
            {
                RequireRead(ref reader);
                ReadKnownMerchants(ref reader, ref known0, ref known1, ref known2, ref known3, ref knownExtra);
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
    /// <param name="merchantId">Merchant id used after parsing to compute <see cref="FraudInput.UnknownMerchant"/>.</param>
    /// <exception cref="JsonException">Thrown when the object shape is invalid.</exception>
    private static void ReadMerchant(ref Utf8JsonReader reader, ref FraudInput input, ref string? merchantId)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException();

        while (RequireRead(ref reader) && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException();

            if (reader.ValueTextEquals("id"u8))
            {
                RequireRead(ref reader);
                merchantId = reader.GetString();
            }
            else if (reader.ValueTextEquals("mcc"u8))
            {
                RequireRead(ref reader);
                input.MccCode = ReadMccCode(ref reader);
            }
            else if (reader.ValueTextEquals("avg_amount"u8))
            {
                RequireRead(ref reader);
                input.MerchantAvgAmount = reader.GetSingle();
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
                input.KmFromHome = reader.GetSingle();
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
                ReadIsoUtc(ref reader, out _, out _, out input.LastMinuteStamp);
            }
            else if (reader.ValueTextEquals("km_from_current"u8))
            {
                RequireRead(ref reader);
                input.KmFromCurrent = reader.GetSingle();
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
    /// <param name="known0">First known merchant slot.</param>
    /// <param name="known1">Second known merchant slot.</param>
    /// <param name="known2">Third known merchant slot.</param>
    /// <param name="known3">Fourth known merchant slot.</param>
    /// <param name="knownExtra">Spill list for merchants after the first four.</param>
    /// <exception cref="JsonException">Thrown when the token is not an array.</exception>
    private static void ReadKnownMerchants(
        ref Utf8JsonReader reader,
        ref string? known0,
        ref string? known1,
        ref string? known2,
        ref string? known3,
        ref List<string>? knownExtra)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException();

        int count = 0;
        while (RequireRead(ref reader) && reader.TokenType != JsonTokenType.EndArray)
        {
            string merchant = reader.GetString() ?? string.Empty;
            switch (count++)
            {
                case 0: known0 = merchant; break;
                case 1: known1 = merchant; break;
                case 2: known2 = merchant; break;
                case 3: known3 = merchant; break;
                default:
                    knownExtra ??= new List<string>(4);
                    knownExtra.Add(merchant);
                    break;
            }
        }
    }

    /// <summary>
    /// Checks whether a merchant id matches any known merchant slot.
    /// </summary>
    /// <param name="merchantId">Merchant id from the request merchant object.</param>
    /// <param name="known0">First known merchant slot.</param>
    /// <param name="known1">Second known merchant slot.</param>
    /// <param name="known2">Third known merchant slot.</param>
    /// <param name="known3">Fourth known merchant slot.</param>
    /// <param name="knownExtra">Optional spill list for additional known merchants.</param>
    /// <returns><see langword="true"/> when <paramref name="merchantId"/> is present in any slot.</returns>
    private static bool MerchantIsKnown(string? merchantId, string? known0, string? known1, string? known2, string? known3, List<string>? knownExtra)
    {
        if (merchantId is null)
            return false;

        if (merchantId == known0 || merchantId == known1 || merchantId == known2 || merchantId == known3)
            return true;

        if (knownExtra is null)
            return false;

        for (int i = 0; i < knownExtra.Count; i++)
        {
            if (merchantId == knownExtra[i])
                return true;
        }

        return false;
    }

    /// <summary>
    /// Reads an ISO UTC JSON string into hour, Monday-based weekday, and absolute minute stamp.
    /// </summary>
    /// <param name="reader">JSON reader positioned on the timestamp string token.</param>
    /// <param name="hour">Parsed UTC hour from 0 through 23.</param>
    /// <param name="dayOfWeek">Parsed Monday-based weekday from 0 through 6.</param>
    /// <param name="minuteStamp">Parsed absolute minute stamp used for fast elapsed-time subtraction.</param>
    /// <exception cref="JsonException">Thrown when the token is not a string.</exception>
    private static void ReadIsoUtc(ref Utf8JsonReader reader, out int hour, out int dayOfWeek, out int minuteStamp)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException();

        if (reader.HasValueSequence)
        {
            FraudVectorizer.ParseIsoUtc(reader.GetString()!, out hour, out dayOfWeek, out minuteStamp);
            return;
        }

        FraudVectorizer.ParseIsoUtc(reader.ValueSpan, out hour, out dayOfWeek, out minuteStamp);
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
