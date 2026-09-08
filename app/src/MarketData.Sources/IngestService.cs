using MarketData.Domain;
using MarketData.Sources.TwelveData;
using MarketData.Storage;
using MarketData.Storage.Dynamo;

namespace MarketData.Sources;

/// <summary>
/// Fetches one symbol, short-circuits if the vendor payload is unchanged, un-adjusts the
/// vendor's split-adjusted prices, applies the observation policy, and appends only what
/// was actually learned.
/// </summary>
public sealed class IngestService(
    IPriceSource source,
    IPriceNormaliser normaliser,
    TwelveDataActionsNormaliser actionsNormaliser,
    ObservationPolicy policy,
    IRawStore rawStore,
    ICuratedStore curatedStore,
    ICursorRepository cursors,
    Func<string, DateOnly, CancellationToken, Task<decimal?>>? knownCloseLookup = null)
{
    private const string Dataset = "prices_daily";

    public async Task<IngestResult> IngestSymbolAsync(
        string symbol, DateOnly from, DateOnly to, string ingestId, CancellationToken ct)
    {
        RawEnvelope envelope;

        try
        {
            envelope = await source.FetchDailyBarsAsync(symbol, from, to, ct);
        }
        catch (VendorNoDataException)
        {
            // No rows in the window. Nothing was learned, which is the same outcome as an
            // unchanged payload -- and the normal one on a weekend or before the day's data
            // is published. Recording it as a failure would make most scheduled runs look
            // broken and hide the ones that are.
            return new IngestResult(symbol, SkippedUnchanged: true, 0, null, []);
        }

        var cursor = await cursors.GetAsync(Dataset, symbol, ct);

        // Nothing changed at the vendor, so nothing was learned. No raw object is
        // written: an identical payload is not a new observation.
        if (cursor?.LastContentHash == envelope.ContentHash)
        {
            return new IngestResult(symbol, SkippedUnchanged: true, 0, null, []);
        }

        var runDate = DateOnly.FromDateTime(envelope.ObservedAt.UtcDateTime);
        var rawKey = await rawStore.WriteAsync(envelope, runDate, ct);

        // Splits are fetched in the same run and their key recorded on every price row,
        // so a rebuild un-adjusts with exactly this split history rather than whatever
        // is known at rebuild time.
        var splitsEnvelope = await source.FetchSplitsAsync(symbol, ct);
        var splitsRawKey = await rawStore.WriteAsync(splitsEnvelope, runDate, ct);

        // Dividends are fetched too. They play no part in un-adjusting -- the vendor
        // adjusts for splits only -- but without them PriceAdjustment.SplitsAndDividends
        // would find no dividend rows and silently return a splits-only series.
        var dividendsEnvelope = await source.FetchDividendsAsync(symbol, ct);
        var dividendsRawKey = await rawStore.WriteAsync(dividendsEnvelope, runDate, ct);

        var actions = ToActions(actionsNormaliser.ParseSplits(splitsEnvelope), splitsRawKey)
            .Concat(ToActions(actionsNormaliser.ParseDividends(dividendsEnvelope), dividendsRawKey))
            .OrderBy(a => a.ExDate)
            .ToList();

        IEnumerable<CorporateAction> ToActions(IReadOnlyList<ParsedAction> parsedActions, string key) =>
            parsedActions.Select(a => new CorporateAction(
                symbol, a.ExDate, a.ActionType, a.Ratio, a.Amount, a.Currency,
                source.SourceId,
                // An action's inferred observed_at is its ex-date: it became effective
                // then, and claiming earlier knowledge would let a query see a split
                // before the market did.
                policy.Clock.InferredPublication(a.ExDate),
                ObservationKind.Inferred,
                ingestId, key));

        var parsed = normaliser.Parse(envelope);
        var bars = new List<DailyBar>(parsed.Count);

        foreach (var bar in parsed)
        {
            // The vendor reports prices adjusted for every split up to the fetch instant.
            // Dividing by that factor recovers the price actually quoted on the day.
            var factor = AdjustmentCalculator.SplitFactor(actions, bar.EffectiveDate);

            var close = bar.Close / factor;

            // Prior knowledge is a real per-date lookup when one is supplied. The
            // cursor watermark remains as the fallback: it only ever caught a
            // restatement whose close differed on a bar at or before the watermark,
            // which is the common case but not every one.
            var known = knownCloseLookup is null
                ? (cursor?.LastEffectiveDate is { } last && bar.EffectiveDate <= last
                    ? close
                    : (decimal?)null)
                : await knownCloseLookup(symbol, bar.EffectiveDate, ct);

            if (policy.Decide(bar.EffectiveDate, close, known, envelope.ObservedAt) is not { } decision)
            {
                continue;
            }

            bars.Add(new DailyBar(
                symbol, bar.EffectiveDate,
                bar.Open / factor, bar.High / factor, bar.Low / factor, close,
                (long)(bar.Volume * factor),
                bar.Currency, source.SourceId,
                decision.ObservedAt, decision.Kind,
                ingestId, rawKey, splitsRawKey));
        }

        var curatedKeys = bars.Count > 0
            ? await curatedStore.AppendPricesAsync(bars, ingestId, ct)
            : [];

        if (actions.Count > 0)
        {
            await curatedStore.AppendActionsAsync(actions, ingestId, ct);
        }

        // Carry forward what was already known, then overlay this run's bars. The
        // repository trims to the newest ten on save. Unadjusted closes, matching what a
        // later comparison will be given.
        var recentCloses = new Dictionary<DateOnly, decimal>();

        foreach (var kv in cursor?.RecentCloses ?? new Dictionary<DateOnly, decimal>())
        {
            recentCloses[kv.Key] = kv.Value;
        }

        foreach (var written in bars)
        {
            recentCloses[written.EffectiveDate] = written.Close;
        }

        await cursors.SaveAsync(
            new Cursor(
                Dataset,
                symbol,
                parsed.Count > 0 ? parsed[^1].EffectiveDate : cursor?.LastEffectiveDate,
                envelope.ContentHash,
                recentCloses),
            ct);

        return new IngestResult(
            symbol, SkippedUnchanged: false, bars.Count, rawKey, curatedKeys, actions.Count);
    }
}
