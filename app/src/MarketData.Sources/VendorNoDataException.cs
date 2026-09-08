namespace MarketData.Sources;

/// <summary>
/// The vendor has no rows for the requested window. Not a failure: it is the normal answer
/// on a weekend, a holiday, or any day the vendor has not published yet.
/// </summary>
/// <remarks>
/// Twelve Data signals this with HTTP 400 and a body saying "No data is available on the
/// specified dates", rather than an empty 200. Left unhandled, a daily incremental run
/// records an error on most of its invocations and the genuine failures are lost in the
/// noise.
/// </remarks>
public sealed class VendorNoDataException(string symbol, string dataset, string vendorMessage)
    : Exception($"{symbol}/{dataset}: {vendorMessage}")
{
    public string Symbol { get; } = symbol;

    public string Dataset { get; } = dataset;
}
