using DuckDB.NET.Data;
using MarketData.Domain;

namespace MarketData.Query;

/// <inheritdoc />
public sealed class DuckDbMarketDataQuery(CuratedSource source) : IMarketDataQuery
{
    public async Task<IReadOnlyList<DailyBar>> GetPricesAsync(
        string symbol,
        DateOnly from,
        DateOnly to,
        DateTimeOffset asOf,
        PriceAdjustment adjustment,
        ObservationMode observations,
        CancellationToken ct)
    {
        var bars = await ReadBarsAsync(symbol, from, to, asOf, observations, ct);

        if (adjustment == PriceAdjustment.None || bars.Count == 0)
        {
            return bars;
        }

        // ObservationMode deliberately does not reach this call. Every backfilled action
        // is INFERRED, so filtering them under ObservedOnly would apply a factor of 1.0
        // and return unadjusted prices labelled as adjusted.
        var actions = await ReadActionsAsync(symbol, asOf, ct);

        if (actions.Count == 0)
        {
            return bars;
        }

        // The dividend factor needs the close on the day before an ex-date. Only bars in
        // the requested window are available, so a dividend whose prior close falls
        // outside it contributes nothing rather than a guess.
        var closes = bars.ToDictionary(b => b.EffectiveDate, b => b.Close);
        decimal? CloseOnDayBefore(DateOnly exDate) =>
            closes.TryGetValue(exDate.AddDays(-1), out var close) ? close : null;

        return bars
            .Select(bar =>
            {
                var factor = AdjustmentCalculator.Factor(
                    actions, bar.EffectiveDate, adjustment, CloseOnDayBefore);

                return factor == 1m
                    ? bar
                    : bar with
                    {
                        Open = bar.Open * factor,
                        High = bar.High * factor,
                        Low = bar.Low * factor,
                        Close = bar.Close * factor,
                        Volume = (long)(bar.Volume / factor)
                    };
            })
            .ToList();
    }

    public async Task<IReadOnlyList<CorporateAction>> GetCorporateActionsAsync(
        string symbol, DateOnly from, DateOnly to, DateTimeOffset asOf, CancellationToken ct)
    {
        var actions = await ReadActionsAsync(symbol, asOf, ct);

        return actions.Where(a => a.ExDate >= from && a.ExDate <= to).ToList();
    }

    private async Task<IReadOnlyList<DailyBar>> ReadBarsAsync(
        string symbol,
        DateOnly from,
        DateOnly to,
        DateTimeOffset asOf,
        ObservationMode observations,
        CancellationToken ct)
    {
        // One window function is the whole temporal model: keep rows observed at or before
        // asOf, then take the most recent row per (symbol, effective_date). The IngestId
        // tiebreak makes the choice deterministic when two rows share an ObservedAt.
        const string Sql = """
            WITH visible AS (
              SELECT *, ROW_NUMBER() OVER (
                PARTITION BY Symbol, EffectiveDate
                ORDER BY ObservedAt DESC, IngestId DESC) AS rn
              FROM read_parquet($glob)
              WHERE Symbol = $symbol
                AND ObservedAt <= $asOf
                AND ($allKinds OR ObservedAtKind = 'OBSERVED')
            )
            SELECT Symbol, EffectiveDate, Open, High, Low, Close, Volume, Currency, Source,
                   ObservedAt, ObservedAtKind, IngestId, RawKey, SplitsRawKey
            FROM visible
            WHERE rn = 1
              AND EffectiveDate BETWEEN $from AND $to
            ORDER BY EffectiveDate
            """;

        await using var connection = Open();

        using var command = connection.CreateCommand();
        command.CommandText = Sql;
        Add(command, "glob", source.Glob("prices_daily"));
        Add(command, "symbol", symbol);
        Add(command, "asOf", asOf.UtcDateTime);
        Add(command, "allKinds", observations == ObservationMode.All);
        Add(command, "from", from.ToDateTime(TimeOnly.MinValue));
        Add(command, "to", to.ToDateTime(TimeOnly.MinValue));

        var bars = new List<DailyBar>();

        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                bars.Add(new DailyBar(
                    Symbol: reader.GetString(0),
                    EffectiveDate: reader.GetFieldValue<DateOnly>(1),
                    Open: reader.GetDecimal(2),
                    High: reader.GetDecimal(3),
                    Low: reader.GetDecimal(4),
                    Close: reader.GetDecimal(5),
                    Volume: reader.GetInt64(6),
                    Currency: reader.GetString(7),
                    Source: reader.GetString(8),
                    ObservedAt: new DateTimeOffset(reader.GetDateTime(9), TimeSpan.Zero),
                    ObservedAtKind: Enum.Parse<ObservationKind>(reader.GetString(10), ignoreCase: true),
                    IngestId: reader.GetString(11),
                    RawKey: reader.GetString(12),
                    SplitsRawKey: reader.GetString(13)));
            }
        }
        catch (DuckDBException ex) when (IsMissingFiles(ex))
        {
            // An empty store is a valid state, not an error: a symbol may simply never
            // have been ingested.
            return [];
        }

        return bars;
    }

    /// <summary>
    /// Every action visible at <paramref name="asOf"/>, unfiltered by ex-date, because
    /// adjusting a bar needs actions from outside the requested window.
    /// </summary>
    private async Task<IReadOnlyList<CorporateAction>> ReadActionsAsync(
        string symbol, DateTimeOffset asOf, CancellationToken ct)
    {
        const string Sql = """
            WITH visible AS (
              SELECT *, ROW_NUMBER() OVER (
                PARTITION BY Symbol, ExDate, ActionType
                ORDER BY ObservedAt DESC, IngestId DESC) AS rn
              FROM read_parquet($glob)
              WHERE Symbol = $symbol
                AND ObservedAt <= $asOf
            )
            SELECT Symbol, ExDate, ActionType, Ratio, Amount, Currency, Source,
                   ObservedAt, ObservedAtKind, IngestId, RawKey
            FROM visible
            WHERE rn = 1
            ORDER BY ExDate
            """;

        await using var connection = Open();

        using var command = connection.CreateCommand();
        command.CommandText = Sql;
        Add(command, "glob", source.Glob("corporate_actions"));
        Add(command, "symbol", symbol);
        Add(command, "asOf", asOf.UtcDateTime);

        var actions = new List<CorporateAction>();

        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                actions.Add(new CorporateAction(
                    Symbol: reader.GetString(0),
                    ExDate: reader.GetFieldValue<DateOnly>(1),
                    ActionType: Enum.Parse<CorporateActionType>(reader.GetString(2), ignoreCase: true),
                    Ratio: reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                    Amount: reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                    Currency: reader.GetString(5),
                    Source: reader.GetString(6),
                    ObservedAt: new DateTimeOffset(reader.GetDateTime(7), TimeSpan.Zero),
                    ObservedAtKind: Enum.Parse<ObservationKind>(reader.GetString(8), ignoreCase: true),
                    IngestId: reader.GetString(9),
                    RawKey: reader.GetString(10)));
            }
        }
        catch (DuckDBException ex) when (IsMissingFiles(ex))
        {
            // A symbol with no corporate actions is normal, not an error.
            return [];
        }

        return actions;
    }

    // read_parquet over a glob matching nothing raises rather than returning zero rows.
    private static bool IsMissingFiles(DuckDBException ex) =>
        ex.Message.Contains("No files found", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("IO Error", StringComparison.OrdinalIgnoreCase);

    private DuckDBConnection Open()
    {
        var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        source.Setup(connection);

        return connection;
    }

    private static void Add(DuckDBCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
