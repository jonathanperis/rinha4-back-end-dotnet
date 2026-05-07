using System.Text.Json;

/// <summary>
/// Normalization denominators loaded from <c>normalization.json</c>.
/// </summary>
/// <remarks>
/// Values are copied into <see cref="FraudScorer"/> flat fields after loading so
/// request scoring keeps the same direct field access as before the cleanup.
/// </remarks>
internal readonly struct NormalizationConstants
{
    /// <summary>Maximum transaction amount denominator.</summary>
    public readonly float MaxAmount;

    /// <summary>Maximum installments denominator.</summary>
    public readonly int MaxInstallments;

    /// <summary>Amount-versus-customer-average ratio denominator.</summary>
    public readonly float AmountVsAvgRatio;

    /// <summary>Maximum minutes-since-last-transaction denominator.</summary>
    public readonly int MaxMinutes;

    /// <summary>Maximum distance denominator for kilometer features.</summary>
    public readonly int MaxKm;

    /// <summary>Maximum 24-hour transaction count denominator.</summary>
    public readonly int MaxTxCount24h;

    /// <summary>Maximum merchant average amount denominator.</summary>
    public readonly int MaxMerchantAvgAmount;

    /// <summary>
    /// Creates a normalization constant set from parsed JSON values.
    /// </summary>
    private NormalizationConstants(
        float maxAmount,
        int maxInstallments,
        float amountVsAvgRatio,
        int maxMinutes,
        int maxKm,
        int maxTxCount24h,
        int maxMerchantAvgAmount)
    {
        MaxAmount = maxAmount;
        MaxInstallments = maxInstallments;
        AmountVsAvgRatio = amountVsAvgRatio;
        MaxMinutes = maxMinutes;
        MaxKm = maxKm;
        MaxTxCount24h = maxTxCount24h;
        MaxMerchantAvgAmount = maxMerchantAvgAmount;
    }

    /// <summary>
    /// Loads all scalar normalization denominators from disk.
    /// </summary>
    /// <param name="path">Path to <c>normalization.json</c>.</param>
    /// <returns>Parsed normalization constants.</returns>
    public static NormalizationConstants Load(string path)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement norms = doc.RootElement;

        return new NormalizationConstants(
            norms.GetProperty("max_amount").GetSingle(),
            norms.GetProperty("max_installments").GetInt32(),
            norms.GetProperty("amount_vs_avg_ratio").GetSingle(),
            norms.GetProperty("max_minutes").GetInt32(),
            norms.GetProperty("max_km").GetInt32(),
            norms.GetProperty("max_tx_count_24h").GetInt32(),
            norms.GetProperty("max_merchant_avg_amount").GetInt32());
    }
}
