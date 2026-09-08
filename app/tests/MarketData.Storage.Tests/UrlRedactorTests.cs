using MarketData.Storage;
using Shouldly;

namespace MarketData.Storage.Tests;

public sealed class UrlRedactorTests
{
    [Fact]
    public void Redacts_apikey_and_keeps_everything_else()
    {
        const string url =
            "https://api.twelvedata.com/time_series?symbol=AAPL&interval=1day&apikey=abc123secret";

        UrlRedactor.Redact(url)
            .ShouldBe("https://api.twelvedata.com/time_series?symbol=AAPL&interval=1day&apikey=REDACTED");
    }

    [Theory]
    [InlineData("api_key")]
    [InlineData("token")]
    [InlineData("APIKEY")]
    public void Redacts_every_sensitive_parameter_name(string name)
    {
        UrlRedactor.Redact($"https://x.test/a?{name}=s3cret").ShouldNotContain("s3cret");
    }

    [Fact]
    public void Leaves_a_url_without_a_query_untouched()
    {
        UrlRedactor.Redact("https://x.test/a").ShouldBe("https://x.test/a");
    }

    [Fact]
    public void Is_idempotent()
    {
        var once = UrlRedactor.Redact("https://x.test/a?apikey=s3cret");

        UrlRedactor.Redact(once).ShouldBe(once);
    }
}

public sealed class ContentHashTests
{
    [Fact]
    public void Is_stable_for_identical_input()
    {
        ContentHash.Sha256("{\"a\":1}").ShouldBe(ContentHash.Sha256("{\"a\":1}"));
    }

    [Fact]
    public void Differs_for_different_input()
    {
        ContentHash.Sha256("{\"a\":1}").ShouldNotBe(ContentHash.Sha256("{\"a\":2}"));
    }

    [Fact]
    public void Is_prefixed_and_lowercase_hex()
    {
        var hash = ContentHash.Sha256("x");

        hash.ShouldStartWith("sha256:");
        hash.Length.ShouldBe("sha256:".Length + 64);
        hash.ShouldBe(hash.ToLowerInvariant());
    }
}
