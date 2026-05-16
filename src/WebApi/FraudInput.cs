/// <summary>
/// Compact request model containing only fields needed by fraud vector construction.
/// </summary>
/// <remarks>
/// The raw parser fills this struct directly instead of materializing the full
/// request contract. Fields are public to keep writes direct and avoid property
/// overhead in the hot parser/scorer boundary.
/// </remarks>
internal struct FraudInput
{
    /// <summary>Transaction amount before normalization.</summary>
    public double Amount;

    /// <summary>Installment count before normalization.</summary>
    public int Installments;

    /// <summary>UTC request hour from 0 through 23.</summary>
    public int Hour;

    /// <summary>Monday-based request weekday from 0 through 6.</summary>
    public int DayOfWeek;

    /// <summary>Absolute UTC second stamp used to subtract last-transaction time.</summary>
    public int RequestedSecondStamp;

    /// <summary>Customer historical average amount before normalization.</summary>
    public double CustomerAvgAmount;

    /// <summary>Customer transaction count in the previous 24 hours.</summary>
    public int TxCount24h;

    /// <summary>Parsed numeric merchant category code, or <c>-1</c> when invalid.</summary>
    public int MccCode;

    /// <summary>Merchant historical average amount before normalization.</summary>
    public double MerchantAvgAmount;

    /// <summary>Whether the terminal reports an online transaction.</summary>
    public bool IsOnline;

    /// <summary>Whether the card was physically present.</summary>
    public bool CardPresent;

    /// <summary>Distance in kilometers from customer home before normalization.</summary>
    public double KmFromHome;

    /// <summary>Whether the nullable last-transaction object was present.</summary>
    public bool HasLastTransaction;

    /// <summary>Absolute UTC second stamp for the last transaction.</summary>
    public int LastSecondStamp;

    /// <summary>Distance in kilometers from the last transaction before normalization.</summary>
    public double KmFromCurrent;

    /// <summary>Whether the merchant id is absent from the customer's known merchants.</summary>
    public bool UnknownMerchant;
}
