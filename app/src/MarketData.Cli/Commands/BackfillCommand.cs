using MarketData.Sources;

namespace MarketData.Cli.Commands;

/// <summary>
/// Fetches full history for one or more symbols. Paces itself against the vendor's
/// per-minute limit rather than leaving that in the operator's head — exceeding it returns
/// 429s that would surface as missing data rather than an obvious failure.
/// </summary>
public static class BackfillCommand
{
    /// <summary>Prices, splits and dividends: three requests per symbol.</summary>
    public const int CreditsPerSymbol = 3;

    public static async Task RunAsync(
        IReadOnlyList<string> symbols,
        DateOnly from,
        DateOnly to,
        RateLimit limits,
        Func<string, DateOnly, DateOnly, CancellationToken, Task<int>> ingest,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken ct)
    {
        var pause = TimeSpan.FromSeconds(
            60.0 * CreditsPerSymbol / Math.Max(1, limits.RequestsPerMinute));

        for (var i = 0; i < symbols.Count; i++)
        {
            if (i > 0)
            {
                await delay(pause, ct);
            }

            var symbol = WatchlistCommand.Normalise(symbols[i]);
            var rows = await ingest(symbol, from, to, ct);

            Console.WriteLine($"{symbol}: {rows} row(s) written");
        }
    }
}
