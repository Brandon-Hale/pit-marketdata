using DuckDB.NET.Data;

namespace MarketData.Query;

/// <summary>
/// Where curated Parquet lives, and whatever DuckDB needs in order to read it. The two
/// forms must return identical rows for identical data; an integration test asserts it.
/// </summary>
public sealed class CuratedSource
{
    private readonly string _prefix;
    private readonly bool _isS3;
    private readonly string? _endpoint;

    private CuratedSource(string prefix, bool isS3, string? endpoint = null)
    {
        _prefix = prefix;
        _isS3 = isS3;
        _endpoint = endpoint;
    }

    /// <summary>A directory on disk. Used by tests and offline work.</summary>
    public static CuratedSource Local(string root) =>
        new(root.Replace('\\', '/').TrimEnd('/'), isS3: false);

    /// <summary>
    /// An S3 bucket, read in place through DuckDB's httpfs extension.
    /// <paramref name="endpoint"/> overrides the AWS endpoint for LocalStack; production
    /// leaves it null.
    /// </summary>
    public static CuratedSource S3(string bucket, string? endpoint = null) =>
        new($"s3://{bucket}", isS3: true, endpoint);

    /// <summary>Glob matching every Parquet part of one dataset.</summary>
    public string Glob(string dataset) =>
        $"{_prefix}/curated/dataset={dataset}/**/*.parquet";

    /// <summary>Loads and configures whatever the connection needs before querying.</summary>
    public void Setup(DuckDBConnection connection)
    {
        if (!_isS3)
        {
            return;
        }

        using var command = connection.CreateCommand();

        // INSTALL reaches extensions.duckdb.org on first use and caches under ~/.duckdb.
        // Acceptable locally and in CI; a Lambda must ship the extension instead. See the
        // risks section of the Stage 4 design.
        command.CommandText = _endpoint is null
            ? "INSTALL httpfs; LOAD httpfs; " +
              "CREATE OR REPLACE SECRET s3 (TYPE s3, PROVIDER credential_chain);"
            : "INSTALL httpfs; LOAD httpfs; " +
              $"CREATE OR REPLACE SECRET s3 (TYPE s3, KEY_ID 'test', SECRET 'test', " +
              $"ENDPOINT '{_endpoint}', URL_STYLE 'path', USE_SSL false);";

        command.ExecuteNonQuery();
    }
}
