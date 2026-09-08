using MarketData.Storage.Dynamo;

namespace MarketData.Cli.Commands;

/// <summary>
/// Watchlist membership. Deliberately bitemporal: <c>add</c> stamps when, and <c>list</c>
/// takes an as-of, so the tracked universe is itself point-in-time. Without that, every
/// historical query would know about symbols that were not being tracked at the time.
/// </summary>
public static class WatchlistCommand
{
    public static async Task AddAsync(
        IWatchlistRepository watchlist, string symbol, DateTimeOffset at, CancellationToken ct)
    {
        var normalised = Normalise(symbol);
        await watchlist.AddAsync(normalised, at, ct);

        Console.WriteLine($"added {normalised} as at {at:yyyy-MM-dd HH:mm:ss}Z");
    }

    public static async Task RemoveAsync(
        IWatchlistRepository watchlist, string symbol, DateTimeOffset at, CancellationToken ct)
    {
        var normalised = Normalise(symbol);
        await watchlist.RemoveAsync(normalised, at, ct);

        Console.WriteLine($"removed {normalised} as at {at:yyyy-MM-dd HH:mm:ss}Z");
    }

    public static async Task ListAsync(
        IWatchlistRepository watchlist, DateTimeOffset asOf, CancellationToken ct)
    {
        var symbols = await watchlist.ActiveAsync(asOf, ct);

        Console.WriteLine($"tracked as at {asOf:yyyy-MM-dd}: {symbols.Count} symbol(s)");

        foreach (var symbol in symbols)
        {
            Console.WriteLine($"  {symbol}");
        }
    }

    // The store must not end up holding both AAPL and aapl as separate symbols.
    public static string Normalise(string symbol) => symbol.Trim().ToUpperInvariant();
}
