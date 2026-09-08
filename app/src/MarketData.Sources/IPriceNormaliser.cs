using MarketData.Storage;

namespace MarketData.Sources;

/// <summary>
/// Turns a raw envelope into bars. Deliberately pure: no clock, no I/O, no prior
/// state, so re-running it over the raw store reproduces the same result forever.
/// </summary>
public interface IPriceNormaliser
{
    string SourceId { get; }

    IReadOnlyList<ParsedBar> Parse(RawEnvelope raw);
}
