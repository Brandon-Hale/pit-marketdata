using MarketData.Domain;
using Shouldly;

namespace MarketData.Domain.Tests;

public sealed class UsEquityPublicationClockTests
{
    private readonly UsEquityPublicationClock _clock = new();

    [Fact]
    public void Summer_date_uses_edt_utc_minus_4()
    {
        // 2020-06-30 is EDT (UTC-4). 16:15 local -> 20:15 UTC.
        var result = _clock.InferredPublication(new DateOnly(2020, 6, 30));

        result.ShouldBe(new DateTimeOffset(2020, 6, 30, 20, 15, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Winter_date_uses_est_utc_minus_5()
    {
        // 2020-12-30 is EST (UTC-5). 16:15 local -> 21:15 UTC.
        var result = _clock.InferredPublication(new DateOnly(2020, 12, 30));

        result.ShouldBe(new DateTimeOffset(2020, 12, 30, 21, 15, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Always_returns_utc()
    {
        _clock.InferredPublication(new DateOnly(2024, 3, 11)).Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Is_strictly_increasing_across_a_dst_boundary()
    {
        // 2021-11-07 is the EDT->EST transition. Consecutive trading days must
        // still produce strictly increasing instants.
        var before = _clock.InferredPublication(new DateOnly(2021, 11, 5));
        var after = _clock.InferredPublication(new DateOnly(2021, 11, 8));

        after.ShouldBeGreaterThan(before);
    }
}
