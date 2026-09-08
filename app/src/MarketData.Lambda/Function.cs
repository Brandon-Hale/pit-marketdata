using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using MarketData.Hosting;
using MarketData.Sources;
using MarketData.Storage.Dynamo;
using Microsoft.Extensions.DependencyInjection;

namespace MarketData.Lambda;

/// <summary>
/// The scheduled entry point. This is a composition root, so it is the one place allowed to
/// read the real clock and the environment.
/// </summary>
public static class Function
{
    /// <summary>Where a symbol with no cursor starts from. Twelve Data reaches back to 2006.</summary>
    private static readonly DateOnly DefaultBackfillStart = new(2015, 1, 1);

    public static async Task Main()
    {
        var options = MarketDataOptions.FromEnvironment();

        var services = new ServiceCollection();

        // No DuckDB here: prior knowledge comes from the cursor's recent closes. An
        // architecture test enforces that the package cannot acquire DuckDB transitively.
        services.AddSingleton<KnownCloseLookup>(sp =>
        {
            var lookup = CursorPriorKnowledge.From(
                sp.GetRequiredService<ICursorRepository>(), "prices_daily");

            return (symbol, date, ct) => lookup(symbol, date, ct);
        });

        var provider = services.AddMarketData(options).BuildServiceProvider();

        using var handlerWrapper = HandlerWrapper.GetHandlerWrapper(
            (Stream _, ILambdaContext context) => HandleAsync(provider, context),
            new DefaultLambdaJsonSerializer());

        using var bootstrap = new LambdaBootstrap(handlerWrapper);
        await bootstrap.RunAsync();
    }

    /// <summary>Returns a one-line summary, so `aws lambda invoke` shows what happened.</summary>
    private static async Task<string> HandleAsync(IServiceProvider services, ILambdaContext context)
    {
        var time = services.GetRequiredService<TimeProvider>();
        var now = time.GetUtcNow();
        var runId = now.ToString("yyyy-MM-ddTHH:mm:ssZ");

        var ingestService = services.GetRequiredService<IngestService>();
        var cursors = services.GetRequiredService<ICursorRepository>();

        var runner = new IngestRunner(
            services.GetRequiredService<IWatchlistRepository>(),
            async (symbol, ct) =>
            {
                var cursor = await cursors.GetAsync("prices_daily", symbol, ct);
                var from = cursor?.LastEffectiveDate?.AddDays(1) ?? DefaultBackfillStart;
                var to = DateOnly.FromDateTime(now.UtcDateTime);

                var result = await ingestService.IngestSymbolAsync(symbol, from, to, runId, ct);

                return new IngestOutcome(
                    result.RowsWritten, result.ActionsWritten, result.SkippedUnchanged);
            },
            services.GetRequiredService<IRunRecordRepository>(),
            time);

        // Stop a little before the function is killed, so the run record still gets written.
        using var timeout = context.RemainingTime > TimeSpan.FromSeconds(15)
            ? new CancellationTokenSource(context.RemainingTime - TimeSpan.FromSeconds(10))
            : new CancellationTokenSource();

        var record = await runner.RunAsync(runId, timeout.Token);

        var summary =
            $"run {record.RunId}: {record.SymbolsSucceeded}/{record.SymbolsAttempted} symbols, " +
            $"{record.RowsWritten} rows, {record.ActionsWritten} actions, " +
            $"{record.SkippedUnchanged} unchanged, {record.Errors.Count} errors";

        context.Logger.LogInformation(summary);

        foreach (var error in record.Errors)
        {
            context.Logger.LogError(error);
        }

        return summary;
    }
}
