namespace MarketData.Storage;

/// <summary>Builds the documented <c>raw/</c> key layout. Keys always use forward slashes.</summary>
public static class RawKey
{
    public static string For(string sourceId, string dataset, DateOnly runDate, string symbol) =>
        $"raw/source={sourceId}/dataset={dataset}/dt={runDate:yyyy-MM-dd}/{symbol}.json";
}
