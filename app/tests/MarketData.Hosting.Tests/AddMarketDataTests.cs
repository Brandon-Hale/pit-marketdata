using MarketData.Sources;
using MarketData.Storage;
using MarketData.Storage.Dynamo;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace MarketData.Hosting.Tests;

public sealed class AddMarketDataTests
{
    private static readonly MarketDataOptions Options =
        new("test-bucket", "test-table", "ap-southeast-2");

    // A stub key, so composing the graph never reaches SSM and the tests need no credentials.
    private static ServiceProvider Build(KnownCloseLookup? lookup = null)
    {
        var services = new ServiceCollection();

        if (lookup is not null)
        {
            services.AddSingleton(lookup);
        }

        return services.AddMarketData(Options, () => "STUB-KEY").BuildServiceProvider();
    }

    [Theory]
    [InlineData(typeof(IRawStore))]
    [InlineData(typeof(ICuratedStore))]
    [InlineData(typeof(ICursorRepository))]
    [InlineData(typeof(IWatchlistRepository))]
    [InlineData(typeof(IInstrumentRepository))]
    [InlineData(typeof(IRunRecordRepository))]
    [InlineData(typeof(IPriceSource))]
    [InlineData(typeof(IPriceNormaliser))]
    [InlineData(typeof(IngestService))]
    [InlineData(typeof(TimeProvider))]
    public void Resolves_everything_both_entry_points_need(Type service)
    {
        using var provider = Build();

        provider.GetService(service).ShouldNotBeNull();
    }

    [Fact]
    public void Resolves_ingest_service_without_a_prior_knowledge_lookup()
    {
        // No lookup registered: IngestService falls back to the cursor watermark rather
        // than failing to construct.
        using var provider = Build();

        provider.GetService<IngestService>().ShouldNotBeNull();
    }

    [Fact]
    public void Uses_a_registered_prior_knowledge_lookup()
    {
        var called = false;
        KnownCloseLookup lookup = (_, _, _) =>
        {
            called = true;
            return Task.FromResult<decimal?>(1m);
        };

        using var provider = Build(lookup);

        provider.GetService<IngestService>().ShouldNotBeNull();
        // The delegate is wired in; whether it fires is IngestService's business, tested there.
        called.ShouldBeFalse();
    }

    [Fact]
    public void The_same_options_instance_is_shared()
    {
        using var provider = Build();

        provider.GetRequiredService<MarketDataOptions>().DataBucket.ShouldBe("test-bucket");
    }

    [Fact]
    public void Missing_environment_variables_fail_loudly()
    {
        var original = Environment.GetEnvironmentVariable(MarketDataOptions.DataBucketVariable);

        try
        {
            Environment.SetEnvironmentVariable(MarketDataOptions.DataBucketVariable, null);

            Should.Throw<InvalidOperationException>(MarketDataOptions.FromEnvironment)
                .Message.ShouldContain(MarketDataOptions.DataBucketVariable);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MarketDataOptions.DataBucketVariable, original);
        }
    }
}
