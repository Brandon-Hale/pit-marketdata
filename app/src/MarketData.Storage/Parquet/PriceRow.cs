namespace MarketData.Storage.Parquet;

/// <summary>Persistence shape of one <c>prices_daily</c> row. Public for Parquet.Net reflection.</summary>
public sealed class PriceRow
{
    public string Symbol { get; set; } = string.Empty;
    public DateOnly EffectiveDate { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Volume { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime ObservedAt { get; set; }
    public string ObservedAtKind { get; set; } = string.Empty;
    public string IngestId { get; set; } = string.Empty;
    public string RawKey { get; set; } = string.Empty;
    public string SplitsRawKey { get; set; } = string.Empty;
}
