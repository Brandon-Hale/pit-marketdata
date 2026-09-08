namespace MarketData.Hosting;

/// <summary>
/// Everything both entry points need in order to reach the same data. Resolved from
/// environment variables, so a local run and a deployed run differ only in values.
/// </summary>
public sealed record MarketDataOptions(
    string DataBucket,
    string TableName,
    string Region,
    string ApiKeyParameterName = "/pit-marketdata/twelvedata/apikey")
{
    public const string DataBucketVariable = "MARKETDATA_DATA_BUCKET";
    public const string TableNameVariable = "MARKETDATA_TABLE_NAME";
    public const string RegionVariable = "MARKETDATA_REGION";

    /// <summary>Reads the options from the environment, failing loudly if any is missing.</summary>
    public static MarketDataOptions FromEnvironment() => new(
        Required(DataBucketVariable),
        Required(TableNameVariable),
        Environment.GetEnvironmentVariable(RegionVariable) ?? "ap-southeast-2");

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Environment variable '{name}' is not set. Both the Lambda and the CLI need " +
                "it to reach the right bucket and table; guessing a default would silently " +
                "read or write the wrong store.");
}
