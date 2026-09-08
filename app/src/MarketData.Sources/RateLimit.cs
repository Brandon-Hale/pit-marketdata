namespace MarketData.Sources;

/// <summary>Vendor quota. Both limits bind; the per-minute one is usually the tighter.</summary>
public sealed record RateLimit(int RequestsPerDay, int RequestsPerMinute);
