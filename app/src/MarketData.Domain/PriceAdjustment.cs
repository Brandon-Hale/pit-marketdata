namespace MarketData.Domain;

/// <summary>Which corporate actions to fold into a returned price.</summary>
public enum PriceAdjustment
{
    /// <summary>The price exactly as quoted on the day. The literal historical record.</summary>
    None,

    /// <summary>Scaled by splits knowable as at the query instant.</summary>
    SplitsOnly,

    /// <summary>Scaled by splits and dividends knowable as at the query instant.</summary>
    SplitsAndDividends
}
