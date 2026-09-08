namespace MarketData.Sources;

/// <summary>
/// The vendor plan does not cover this endpoint for this symbol.
/// </summary>
/// <remarks>
/// Twelve Data's free tier serves <c>/time_series</c> for any symbol but restricts
/// <c>/splits</c> and <c>/dividends</c> to AAPL, answering 403 for everything else. Since
/// prices are split-adjusted, ingesting a symbol without its split history would store
/// adjusted prices labelled as unadjusted -- the exact silent corruption this project
/// exists to prevent. So this is never swallowed: either a caller supplies the actions from
/// elsewhere, or the ingest fails.
/// </remarks>
public sealed class VendorNotEntitledException(string symbol, string dataset, string vendorMessage)
    : Exception($"{symbol}/{dataset}: {vendorMessage}")
{
    public string Symbol { get; } = symbol;

    public string Dataset { get; } = dataset;
}
