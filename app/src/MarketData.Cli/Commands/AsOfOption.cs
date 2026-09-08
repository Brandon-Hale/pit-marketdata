using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;

namespace MarketData.Cli.Commands;

/// <summary>
/// An <c>as-of</c> instant parsed as <b>UTC</b> when no offset is given.
/// </summary>
/// <remarks>
/// Every timestamp in this system is UTC, so interpreting a bare date in the machine's local
/// zone is both inconsistent and quietly wrong. In Sydney it shifts the instant ten hours
/// earlier, so <c>--as-of 2024-06-08</c> would exclude a bar published at 20:15 UTC on
/// 2024-06-07 and simply return fewer rows than asked for, with nothing to indicate why.
///
/// An explicit offset is always honoured: <c>2024-06-08T00:00:00+10:00</c> means what it says.
/// </remarks>
public static class AsOfOption
{
    public static DateTimeOffset Parse(string text)
    {
        var value = text.Trim();

        // An explicit offset or a trailing Z is honoured as written.
        if (DateTimeOffset.TryParse(
                value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var explicitOffset) &&
            HasExplicitOffset(value))
        {
            return explicitOffset.ToUniversalTime();
        }

        if (DateTime.TryParse(
                value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var naive))
        {
            return new DateTimeOffset(naive, TimeSpan.Zero);
        }

        throw new FormatException(
            $"Could not read '{text}' as a date or instant. Try 2024-06-08, " +
            "2024-06-08T22:15:00Z, or 2024-06-08T08:00:00+10:00.");
    }

    private static bool HasExplicitOffset(string value) =>
        value.EndsWith('Z') ||
        value.EndsWith('z') ||
        (value.LastIndexOfAny(['+', '-']) is var i && i > 9);

    /// <summary>The required <c>--as-of</c> used by <c>query</c>.</summary>
    public static Option<DateTimeOffset> Required(string description) =>
        new("--as-of")
        {
            Description = description,
            Required = true,
            CustomParser = result => Parse(result.Tokens[0].Value)
        };

    /// <summary>An optional <c>--as-of</c>, for commands where defaulting to now is safe.</summary>
    public static Option<DateTimeOffset?> Optional(string description) =>
        new("--as-of")
        {
            Description = description,
            CustomParser = result =>
                result.Tokens.Count == 0 ? null : Parse(result.Tokens[0].Value)
        };
}
