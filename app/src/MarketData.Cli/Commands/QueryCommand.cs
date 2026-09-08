using System.CommandLine;
using MarketData.Domain;

namespace MarketData.Cli.Commands;

/// <summary>
/// The as-of read. <c>--as-of</c> is required, deliberately: defaulting it to now is exactly
/// how lookahead bias would re-enter a system that spent four stages designing it out, and a
/// CLI is where such a convenience would most plausibly be added later.
/// </summary>
public static class QueryCommand
{
    public static Command Build(
        Func<string, DateOnly, DateOnly, DateTimeOffset, PriceAdjustment, ObservationMode,
            CancellationToken, Task> run)
    {
        var symbol = new Argument<string>("symbol") { Description = "Ticker, e.g. AAPL." };

        var on = new Option<DateOnly?>("--on")
        {
            Description = "A single trading date. Omit to use --from and --to."
        };
        var from = new Option<DateOnly?>("--from") { Description = "Start of the date range." };
        var to = new Option<DateOnly?>("--to") { Description = "End of the date range." };

        var asOf = AsOfOption.Required(
            "What was knowable at this instant. UTC unless an offset is given. Required.");

        var adjust = new Option<string>("--adjust")
        {
            Description = "none | splits | all. Default: splits.",
            DefaultValueFactory = _ => "splits"
        };

        var observedOnly = new Option<bool>("--observed-only")
        {
            Description = "Exclude bars whose observed_at was reconstructed."
        };

        var command = new Command("query", "Ask what was knowable about a symbol.")
        {
            symbol, on, from, to, asOf, adjust, observedOnly
        };

        command.SetAction((parseResult, ct) =>
        {
            var single = parseResult.GetValue(on);
            var start = single ?? parseResult.GetValue(from)
                ?? throw new InvalidOperationException("Supply --on, or --from and --to.");
            var end = single ?? parseResult.GetValue(to) ?? start;

            return run(
                WatchlistCommand.Normalise(parseResult.GetValue(symbol)!),
                start, end,
                parseResult.GetValue(asOf),
                ParseAdjustment(parseResult.GetValue(adjust)!),
                parseResult.GetValue(observedOnly) ? ObservationMode.ObservedOnly : ObservationMode.All,
                ct);
        });

        return command;
    }

    public static PriceAdjustment ParseAdjustment(string text) => text.ToLowerInvariant() switch
    {
        "none" => PriceAdjustment.None,
        "splits" => PriceAdjustment.SplitsOnly,
        "all" => PriceAdjustment.SplitsAndDividends,
        _ => throw new ArgumentException(
            $"Unknown --adjust value '{text}'. Use none, splits or all.", nameof(text))
    };

    /// <summary>One line per bar, fixed width so a series is scannable.</summary>
    public static void Print(IReadOnlyList<DailyBar> bars)
    {
        if (bars.Count == 0)
        {
            Console.WriteLine("no rows: nothing was knowable about that symbol as at that instant");
            return;
        }

        foreach (var bar in bars)
        {
            Console.WriteLine(
                $"{bar.EffectiveDate:yyyy-MM-dd}  " +
                $"O {bar.Open,10:0.####}  H {bar.High,10:0.####}  " +
                $"L {bar.Low,10:0.####}  C {bar.Close,10:0.####}  " +
                $"V {bar.Volume,12:N0}  [{bar.ObservedAtKind.ToString().ToUpperInvariant()}]");
        }
    }
}
