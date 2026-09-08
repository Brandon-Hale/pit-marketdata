using Amazon.Lambda.Core;
using MarketData.Hosting;
using MarketData.Sources;
using MarketData.Storage.Dynamo;
using Microsoft.Extensions.DependencyInjection;

// The managed .NET runtime hosts this assembly and calls the handler directly, so the
// serializer is declared once here rather than passed to a bootstrap wrapper.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace MarketData.Lambda;

/// <summary>
/// The scheduled entry point, hosted by the managed <c>dotnet10</c> runtime. Handler string:
/// <c>MarketData.Lambda::MarketData.Lambda.Function::HandleAsync</c>.
/// </summary>
/// <remarks>
/// This is a composition root, so it is the one place allowed to read the real clock and the
/// environment. There is deliberately no DuckDB here: prior knowledge comes from the cursor's
/// recent closes, and an architecture test enforces that the package cannot acquire DuckDB
/// transitively.
/// </remarks>
public static class Function
{
    /// <summary>Where a symbol with no cursor starts from. Twelve Data reaches back to 2006.</summary>
    private static readonly DateOnly DefaultBackfillStart = new(2015, 1, 1);

    /// <summary>
    /// Built once and reused across warm invocations. Lazy rather than a static initialiser
    /// so a configuration failure surfaces as a handler error with a usable message, not a
    /// TypeInitializationException wrapping it.
    /// </summary>
    private static readonly Lazy<ServiceProvider> Services = new(BuildServices);

    public static async Task<string> HandleAsync(Stream input, ILambdaContext context)
    {
        var services = Services.Value;

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

    private static ServiceProvider BuildServices()
    {
        var options = MarketDataOptions.FromEnvironment();
        var services = new ServiceCollection();

        services.AddSingleton<KnownCloseLookup>(sp =>
        {
            var lookup = CursorPriorKnowledge.From(
                sp.GetRequiredService<ICursorRepository>(), "prices_daily");

            return (symbol, date, ct) => lookup(symbol, date, ct);
        });

        return services.AddMarketData(options).BuildServiceProvider();
    }
}
