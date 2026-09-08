using System.Globalization;
using System.Text.Json;
using MarketData.Domain;
using MarketData.Storage;

namespace MarketData.Sources.TwelveData;

/// <summary>Parses Twelve Data's split and dividend payloads into a single action model.</summary>
public sealed class TwelveDataActionsNormaliser
{
    public string SourceId => "twelvedata";

    public IReadOnlyList<ParsedAction> ParseSplits(RawEnvelope raw) =>
        Parse(raw, "splits", (element, currency) => new ParsedAction(
            Date(element, "date"),
            CorporateActionType.Split,
            Ratio: SplitRatio(element),
            Amount: null,
            currency));

    public IReadOnlyList<ParsedAction> ParseDividends(RawEnvelope raw) =>
        Parse(raw, "dividends", (element, currency) => new ParsedAction(
            Date(element, "ex_date"),
            CorporateActionType.Dividend,
            Ratio: null,
            Amount: Dec(element, "amount"),
            currency));

    /// <summary>
    /// Derived from <c>to_factor / from_factor</c>, never from the vendor's own
    /// <c>ratio</c> field. Verified 2026-09-08: <c>ratio</c> is rounded — a 7-for-1
    /// split reports 0.14286 rather than 1/7 — while the factors are exact integers.
    /// Trusting <c>ratio</c> pushes that rounding error into every adjustment factor
    /// derived from it. Falls back to <c>ratio</c> only if the factors are absent.
    /// </summary>
    private static decimal SplitRatio(JsonElement element)
    {
        if (element.TryGetProperty("from_factor", out var from)
            && element.TryGetProperty("to_factor", out var to)
            && from.TryGetDecimal(out var fromFactor)
            && to.TryGetDecimal(out var toFactor)
            && fromFactor != 0m)
        {
            return toFactor / fromFactor;
        }

        return Dec(element, "ratio");
    }

    private static IReadOnlyList<ParsedAction> Parse(
        RawEnvelope raw,
        string arrayName,
        Func<JsonElement, string, ParsedAction> map)
    {
        using var document = JsonDocument.Parse(raw.Payload);

        if (!document.RootElement.TryGetProperty(arrayName, out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var currency = document.RootElement.TryGetProperty("meta", out var meta)
                       && meta.TryGetProperty("currency", out var c)
            ? c.GetString() ?? "USD"
            : "USD";

        return items.EnumerateArray()
            .Select(item => map(item, currency))
            .OrderBy(a => a.ExDate)
            .ToList();
    }

    private static DateOnly Date(JsonElement element, string name) =>
        DateOnly.ParseExact(
            element.GetProperty(name).GetString()
            ?? throw new InvalidDataException($"Property '{name}' was null."),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture);

    // Numbers arrive unquoted here, unlike the price endpoints. GetDecimal keeps
    // full precision; a double round-trip would not.
    private static decimal Dec(JsonElement element, string name) =>
        element.GetProperty(name).GetDecimal();
}
