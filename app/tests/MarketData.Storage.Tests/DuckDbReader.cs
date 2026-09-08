using DuckDB.NET.Data;

namespace MarketData.Storage.Tests;

/// <summary>In-memory DuckDB used to read Parquet exactly as the query layer will.</summary>
public sealed class DuckDbReader : IDisposable
{
    private readonly DuckDBConnection _connection;

    public DuckDbReader()
    {
        _connection = new DuckDBConnection("DataSource=:memory:");
        _connection.Open();
    }

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;

        using var reader = command.ExecuteReader();
        var rows = new List<IReadOnlyDictionary<string, object?>>();

        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Glob matching every Parquet part of a dataset under a local root.</summary>
    public static string Glob(string root, string dataset) =>
        Path.Combine(root, "curated", $"dataset={dataset}", "**", "*.parquet").Replace('\\', '/');

    public void Dispose() => _connection.Dispose();
}
