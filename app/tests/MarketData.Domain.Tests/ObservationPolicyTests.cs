using MarketData.Domain;
using Shouldly;

namespace MarketData.Domain.Tests;

public sealed class ObservationPolicyTests
{
    private readonly ObservationPolicy _policy = new(new UsEquityPublicationClock(), TimeSpan.FromHours(24));

    private static readonly DateOnly Day = new(2020, 8, 31);

    [Fact]
    public void First_sight_long_after_publication_is_inferred()
    {
        var fetchedAt = new DateTimeOffset(2026, 9, 8, 22, 15, 0, TimeSpan.Zero);

        var decision = _policy.Decide(Day, 129.04m, knownClose: null, fetchedAt);

        decision.ShouldNotBeNull();
        decision.Kind.ShouldBe(ObservationKind.Inferred);
        decision.ObservedAt.ShouldBe(new DateTimeOffset(2020, 8, 31, 20, 15, 0, TimeSpan.Zero));
    }

    [Fact]
    public void First_sight_inside_the_live_window_is_observed()
    {
        // Published 2020-08-31 20:15Z; fetched two hours later.
        var fetchedAt = new DateTimeOffset(2020, 8, 31, 22, 15, 0, TimeSpan.Zero);

        var decision = _policy.Decide(Day, 129.04m, knownClose: null, fetchedAt);

        decision.ShouldNotBeNull();
        decision.Kind.ShouldBe(ObservationKind.Observed);
        decision.ObservedAt.ShouldBe(fetchedAt);
    }

    [Fact]
    public void An_unchanged_value_is_not_re_emitted()
    {
        var fetchedAt = new DateTimeOffset(2026, 9, 8, 22, 15, 0, TimeSpan.Zero);

        _policy.Decide(Day, 129.04m, knownClose: 129.04m, fetchedAt).ShouldBeNull();
    }

    [Fact]
    public void A_restatement_takes_the_real_fetch_time_not_an_inferred_one()
    {
        var fetchedAt = new DateTimeOffset(2027, 3, 1, 10, 0, 0, TimeSpan.Zero);

        var decision = _policy.Decide(Day, 128.50m, knownClose: 129.04m, fetchedAt);

        decision.ShouldNotBeNull();
        decision.Kind.ShouldBe(ObservationKind.Observed);
        decision.ObservedAt.ShouldBe(fetchedAt);
    }
}
