using System.CommandLine;
using MarketData.Cli.Commands;
using MarketData.Domain;
using MarketData.Hosting;
using MarketData.Query;
using MarketData.Sources;
using MarketData.Storage.Dynamo;
using Microsoft.Extensions.DependencyInjection;

// This is a composition root, so it is one of the two places allowed to read the real clock
// and the environment.
var options = MarketDataOptions.FromEnvironment();

var services = new ServiceCollection();

// The CLI does carry DuckDB, so its prior knowledge is the real per-date lookup through the
// query layer rather than the Lambda's ten-day cursor window.
services.AddSingleton<IMarketDataQuery>(_ =>
    new DuckDbMarketDataQuery(CuratedSource.S3(options.DataBucket)));

services.AddSingleton<KnownCloseLookup>(sp =>
{
    var lookup = PriorKnowledge.From(
        sp.GetRequiredService<IMarketDataQuery>(), sp.GetRequiredService<TimeProvider>());

    return (symbol, date, ct) => lookup(symbol, date, ct);
});

var provider = services.AddMarketData(options).BuildServiceProvider();

var time = provider.GetRequiredService<TimeProvider>();
var watchlist = provider.GetRequiredService<IWatchlistRepository>();

// ---- watchlist -------------------------------------------------------------------------

var symbolArgument = new Argument<string>("symbol") { Description = "Ticker, e.g. AAPL." };

var addCommand = new Command("add", "Start tracking a symbol.") { symbolArgument };
addCommand.SetAction((parseResult, ct) => WatchlistCommand.AddAsync(
    watchlist, parseResult.GetValue(symbolArgument)!, time.GetUtcNow(), ct));

var removeCommand = new Command("remove", "Stop tracking a symbol.") { symbolArgument };
removeCommand.SetAction((parseResult, ct) => WatchlistCommand.RemoveAsync(
    watchlist, parseResult.GetValue(symbolArgument)!, time.GetUtcNow(), ct));

// Membership defaulting to now is a convenience; a price defaulting to now is lookahead
// bias. Hence a default here and none on query.
var listAsOf = new Option<DateTimeOffset?>("--as-of")
{
    Description = "Show membership as it was at this instant. Defaults to now."
};

var listCommand = new Command("list", "Show tracked symbols.") { listAsOf };
listCommand.SetAction((parseResult, ct) => WatchlistCommand.ListAsync(
    watchlist, parseResult.GetValue(listAsOf) ?? time.GetUtcNow(), ct));

var watchlistCommand = new Command("watchlist", "Manage the tracked universe.")
{
    addCommand, removeCommand, listCommand
};

// ---- backfill --------------------------------------------------------------------------

var backfillFrom = new Option<DateOnly>("--from")
{
    Description = "First trading date to fetch.",
    Required = true
};

var backfillTo = new Option<DateOnly?>("--to")
{
    Description = "Last trading date to fetch. Defaults to today."
};

var backfillCommand = new Command("backfill", "Fetch full history for a symbol.")
{
    symbolArgument, backfillFrom, backfillTo
};

backfillCommand.SetAction(async (parseResult, ct) =>
{
    var ingestService = provider.GetRequiredService<IngestService>();
    var source = provider.GetRequiredService<IPriceSource>();
    var runId = $"cli-{time.GetUtcNow():yyyyMMddTHHmmssZ}";

    await BackfillCommand.RunAsync(
        [parseResult.GetValue(symbolArgument)!],
        parseResult.GetValue(backfillFrom),
        parseResult.GetValue(backfillTo) ?? DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime),
        source.Limits,
        async (symbol, from, to, token) =>
        {
            var result = await ingestService.IngestSymbolAsync(symbol, from, to, runId, token);
            return result.RowsWritten;
        },
        Task.Delay,
        ct);
});

// ---- query -----------------------------------------------------------------------------

var queryCommand = QueryCommand.Build(async (symbol, from, to, asOf, adjustment, mode, ct) =>
{
    var query = provider.GetRequiredService<IMarketDataQuery>();
    var bars = await query.GetPricesAsync(symbol, from, to, asOf, adjustment, mode, ct);

    QueryCommand.Print(bars);
});

var root = new RootCommand("Point-in-time market data warehouse.")
{
    watchlistCommand, backfillCommand, queryCommand
};

return await root.Parse(args).InvokeAsync();
