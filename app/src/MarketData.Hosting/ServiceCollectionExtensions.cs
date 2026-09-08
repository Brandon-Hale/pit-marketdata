using Amazon;
using Amazon.DynamoDBv2;
using Amazon.S3;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using MarketData.Domain;
using MarketData.Sources;
using MarketData.Sources.TwelveData;
using MarketData.Storage;
using MarketData.Storage.Dynamo;
using MarketData.Storage.S3;
using Microsoft.Extensions.DependencyInjection;

namespace MarketData.Hosting;

/// <summary>The delegate <see cref="IngestService"/> uses to ask what a close already was.</summary>
public delegate Task<decimal?> KnownCloseLookup(string symbol, DateOnly date, CancellationToken ct);

/// <summary>
/// The wiring both entry points share. Anything specific to one of them — the query layer
/// for the CLI, the cursor-backed prior knowledge for the Lambda — is registered by that
/// entry point, not here.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <param name="apiKeyFactory">
    /// Supplies the vendor key. Defaults to reading SSM, which needs AWS credentials; tests
    /// and offline runs pass their own rather than reaching the network during composition.
    /// </param>
    public static IServiceCollection AddMarketData(
        this IServiceCollection services,
        MarketDataOptions options,
        Func<string>? apiKeyFactory = null)
    {
        var region = RegionEndpoint.GetBySystemName(options.Region);

        services.AddSingleton(options);
        services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(region));
        services.AddSingleton<IAmazonDynamoDB>(_ => new AmazonDynamoDBClient(region));

        services.AddSingleton<IRawStore>(sp =>
            new S3RawStore(sp.GetRequiredService<IAmazonS3>(), options.DataBucket));
        services.AddSingleton<ICuratedStore>(sp =>
            new S3CuratedStore(sp.GetRequiredService<IAmazonS3>(), options.DataBucket));

        services.AddSingleton<ICursorRepository>(sp =>
            new CursorRepository(sp.GetRequiredService<IAmazonDynamoDB>(), options.TableName));
        services.AddSingleton<IWatchlistRepository>(sp =>
            new WatchlistRepository(sp.GetRequiredService<IAmazonDynamoDB>(), options.TableName));
        services.AddSingleton<IInstrumentRepository>(sp =>
            new InstrumentRepository(sp.GetRequiredService<IAmazonDynamoDB>(), options.TableName));
        services.AddSingleton<IRunRecordRepository>(sp =>
            new RunRecordRepository(sp.GetRequiredService<IAmazonDynamoDB>(), options.TableName));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPublicationClock, UsEquityPublicationClock>();
        services.AddSingleton(sp => new ObservationPolicy(
            sp.GetRequiredService<IPublicationClock>(), TimeSpan.FromHours(24)));

        services.AddSingleton<IPriceNormaliser, TwelveDataPriceNormaliser>();
        services.AddSingleton<TwelveDataActionsNormaliser>();

        // The key is read from SSM rather than an environment variable so it never appears
        // in the Lambda's configuration, where the console and GetFunctionConfiguration
        // would show it. Lazy so composing the graph makes no network call.
        var key = new Lazy<string>(apiKeyFactory ?? (() => ReadApiKeyFromSsm(options)));

        services.AddSingleton<IPriceSource>(sp => new TwelveDataPriceSource(
            new HttpClient
            {
                BaseAddress = new Uri("https://api.twelvedata.com/"),
                Timeout = TimeSpan.FromSeconds(30)
            },
            key.Value,
            sp.GetRequiredService<TimeProvider>()));

        // One construction path. Which prior knowledge it gets is the entry point's choice:
        // the CLI registers the query-layer lookup, the Lambda the cursor-backed one, and
        // when neither is registered IngestService falls back to the cursor watermark.
        services.AddSingleton(sp => new IngestService(
            sp.GetRequiredService<IPriceSource>(),
            sp.GetRequiredService<IPriceNormaliser>(),
            sp.GetRequiredService<TwelveDataActionsNormaliser>(),
            sp.GetRequiredService<ObservationPolicy>(),
            sp.GetRequiredService<IRawStore>(),
            sp.GetRequiredService<ICuratedStore>(),
            sp.GetRequiredService<ICursorRepository>(),
            sp.GetService<KnownCloseLookup>() is { } lookup
                ? (symbol, date, ct) => lookup(symbol, date, ct)
                : null));

        return services;
    }

    private static string ReadApiKeyFromSsm(MarketDataOptions options)
    {
        using var ssm = new AmazonSimpleSystemsManagementClient(
            RegionEndpoint.GetBySystemName(options.Region));

        var response = ssm.GetParameterAsync(new GetParameterRequest
        {
            Name = options.ApiKeyParameterName,
            WithDecryption = true
        }).GetAwaiter().GetResult();

        return response.Parameter.Value;
    }
}
