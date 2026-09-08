namespace MarketData.Storage.Parquet;

/// <summary>Persistence shape of one <c>corporate_actions</c> row.</summary>
public sealed class CorporateActionRow
{
    public string Symbol { get; set; } = string.Empty;
    public DateOnly ExDate { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public decimal? Ratio { get; set; }
    public decimal? Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime ObservedAt { get; set; }
    public string ObservedAtKind { get; set; } = string.Empty;
    public string IngestId { get; set; } = string.Empty;
    public string RawKey { get; set; } = string.Empty;
}
