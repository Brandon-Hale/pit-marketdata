using System.CommandLine;
using MarketData.Domain;
using MarketData.Storage;

namespace MarketData.Cli.Commands;

/// <summary>
/// Records a corporate action by hand, for symbols the vendor plan does not cover.
/// </summary>
/// <remarks>
/// Twelve Data's free tier serves splits and dividends for AAPL only. Since its prices are
/// split-adjusted, every other symbol needs its split history from somewhere or the stored
/// prices would be adjusted values labelled as unadjusted.
///
/// A hand-entered action is a first-class fact, not a workaround: it carries the same
/// <c>observed_at</c>, the same <c>INFERRED</c> kind, and the same append-only semantics as
/// a vendor one. The only difference is <c>source = MANUAL</c>, which is recorded so the
/// provenance is never in doubt.
/// </remarks>
public static class ActionCommand
{
    public const string ManualSource = "MANUAL";

    public static Command Build(
        Func<CorporateAction, CancellationToken, Task> save,
        IPublicationClock clock,
        Func<DateTimeOffset> now)
    {
        var symbol = new Argument<string>("symbol") { Description = "Ticker, e.g. NVDA." };

        var exDate = new Option<DateOnly>("--ex-date")
        {
            Description = "The date the action took effect. Required.",
            Required = true
        };

        var split = new Option<bool>("--split") { Description = "Record a split." };
        var dividend = new Option<bool>("--dividend") { Description = "Record a dividend." };

        // Factors rather than a ratio: a 7-for-1 split is exactly 1/7, and any decimal the
        // user typed would carry rounding into every adjusted price derived from it.
        var fromFactor = new Option<int>("--from")
        {
            Description = "Shares before, e.g. 1 for a 10-for-1 split."
        };
        var toFactor = new Option<int>("--to")
        {
            Description = "Shares after, e.g. 10 for a 10-for-1 split."
        };

        var amount = new Option<decimal?>("--amount")
        {
            Description = "Dividend amount per share."
        };

        var currency = new Option<string>("--currency")
        {
            Description = "Currency. Default USD.",
            DefaultValueFactory = _ => "USD"
        };

        var command = new Command("add", "Record a corporate action by hand.")
        {
            symbol, exDate, split, dividend, fromFactor, toFactor, amount, currency
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var isSplit = parseResult.GetValue(split);
            var isDividend = parseResult.GetValue(dividend);

            if (isSplit == isDividend)
            {
                throw new InvalidOperationException("Supply exactly one of --split or --dividend.");
            }

            var date = parseResult.GetValue(exDate);
            var ticker = WatchlistCommand.Normalise(parseResult.GetValue(symbol)!);

            decimal? ratio = null;
            decimal? value = null;

            if (isSplit)
            {
                var from = parseResult.GetValue(fromFactor);
                var to = parseResult.GetValue(toFactor);

                if (from <= 0 || to <= 0)
                {
                    throw new InvalidOperationException(
                        "A split needs --from and --to as positive whole numbers. " +
                        "A 10-for-1 split is --from 1 --to 10.");
                }

                // Same convention as the vendor: the factor an old price is multiplied by.
                ratio = (decimal)from / to;
            }
            else
            {
                value = parseResult.GetValue(amount)
                    ?? throw new InvalidOperationException("A dividend needs --amount.");

                if (value <= 0)
                {
                    throw new InvalidOperationException("--amount must be positive.");
                }
            }

            var action = new CorporateAction(
                Symbol: ticker,
                ExDate: date,
                ActionType: isSplit ? CorporateActionType.Split : CorporateActionType.Dividend,
                Ratio: ratio,
                Amount: value,
                Currency: parseResult.GetValue(currency)!,
                Source: ManualSource,
                // The ex-date is when the action became effective and public, which is the
                // same instant a vendor-sourced action would carry. Using "now" instead
                // would hide the split from every query before today.
                ObservedAt: clock.InferredPublication(date),
                ObservedAtKind: ObservationKind.Inferred,
                IngestId: $"manual-{now():yyyyMMddTHHmmssZ}",
                RawKey: string.Empty);

            await save(action, ct);

            var what = isSplit
                ? $"split, ratio {ratio}"
                : $"dividend, {value} {parseResult.GetValue(currency)}";

            Console.WriteLine($"recorded {ticker} {date:yyyy-MM-dd} {what} (source {ManualSource})");
        });

        return new Command("action", "Record corporate actions the vendor plan does not cover.")
        {
            command
        };
    }
}
