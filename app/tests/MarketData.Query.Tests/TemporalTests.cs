using MarketData.Domain;
using Shouldly;

namespace MarketData.Query.Tests;

/// <summary>
/// The eight tests from §7 of the Layer 1 design. These are the specification: a failure
/// here means the temporal model is wrong. Do not adjust a test to match the behaviour.
/// </summary>
public sealed class TemporalTests : IDisposable
{
    private readonly QueryFixture _fixture = new();

    private static readonly DateOnly Day = new(2024, 6, 14);
    private static readonly DateOnly From = new(2024, 1, 1);
    private static readonly DateOnly To = new(2024, 12, 31);

    private Task<IReadOnlyList<DailyBar>> Read(
        DateTimeOffset asOf,
        PriceAdjustment adjustment = PriceAdjustment.None,
        ObservationMode mode = ObservationMode.All) =>
        _fixture.Query.GetPricesAsync(
            "AAPL", From, To, asOf, adjustment, mode, TestContext.Current.CancellationToken);

    [Fact]
    public async Task T1_no_lookahead()
    {
        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(Day, 100m, new DateTimeOffset(2024, 7, 1, 0, 0, 0, TimeSpan.Zero)));

        var bars = await Read(new DateTimeOffset(2024, 6, 30, 0, 0, 0, TimeSpan.Zero));

        bars.ShouldBeEmpty();
    }

    [Fact]
    public async Task T2_restatement_visibility()
    {
        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(Day, 100m, new DateTimeOffset(2024, 6, 14, 20, 15, 0, TimeSpan.Zero), ingestId: "run-1"),
            QueryFixture.Bar(Day, 101m, new DateTimeOffset(2024, 8, 1, 0, 0, 0, TimeSpan.Zero), ingestId: "run-2"));

        var between = await Read(new DateTimeOffset(2024, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var after = await Read(new DateTimeOffset(2024, 9, 1, 0, 0, 0, TimeSpan.Zero));

        between.Single().Close.ShouldBe(100m);
        after.Single().Close.ShouldBe(101m);
    }

    [Fact]
    public async Task T3_reproducibility_across_later_ingests()
    {
        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(Day, 100m, new DateTimeOffset(2024, 6, 14, 20, 15, 0, TimeSpan.Zero), ingestId: "run-1"));

        var asOf = new DateTimeOffset(2024, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var before = await Read(asOf);

        // A later ingest arrives, carrying a revision the as-of must not see.
        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(Day, 101m, new DateTimeOffset(2024, 8, 1, 0, 0, 0, TimeSpan.Zero), ingestId: "run-2"));

        var after = await Read(asOf);

        after.Select(b => b.Close).ShouldBe(before.Select(b => b.Close));
    }

    [Fact]
    public async Task T4_adjustment_is_continuous_across_an_ex_date()
    {
        var dayBefore = new DateOnly(2024, 6, 6);
        var exDate = new DateOnly(2024, 6, 7);

        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(dayBefore, 400m, new DateTimeOffset(2024, 6, 6, 20, 15, 0, TimeSpan.Zero)),
            QueryFixture.Bar(exDate, 100m, new DateTimeOffset(2024, 6, 7, 20, 15, 0, TimeSpan.Zero)));
        await _fixture.WriteActionsAsync(
            QueryFixture.Split(exDate, 0.25m, new DateTimeOffset(2024, 6, 7, 20, 15, 0, TimeSpan.Zero)));

        var bars = await Read(DateTimeOffset.UtcNow, PriceAdjustment.SplitsOnly);

        // 400 x 0.25 = 100, matching the post-split bar: the series is continuous.
        bars.Single(b => b.EffectiveDate == dayBefore).Close.ShouldBe(100m);
        bars.Single(b => b.EffectiveDate == exDate).Close.ShouldBe(100m);
    }

    [Fact]
    public async Task T5_an_action_observed_after_as_of_is_not_applied()
    {
        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(Day, 400m, new DateTimeOffset(2024, 6, 14, 20, 15, 0, TimeSpan.Zero)));
        await _fixture.WriteActionsAsync(
            QueryFixture.Split(new DateOnly(2024, 8, 30), 0.25m,
                new DateTimeOffset(2024, 8, 30, 20, 15, 0, TimeSpan.Zero)));

        var before = await Read(new DateTimeOffset(2024, 7, 1, 0, 0, 0, TimeSpan.Zero), PriceAdjustment.SplitsOnly);
        var after = await Read(new DateTimeOffset(2024, 9, 1, 0, 0, 0, TimeSpan.Zero), PriceAdjustment.SplitsOnly);

        before.Single().Close.ShouldBe(400m);
        after.Single().Close.ShouldBe(100m);
    }

    [Fact]
    public async Task T6_deterministic_tiebreak_on_identical_observed_at()
    {
        var same = new DateTimeOffset(2024, 6, 14, 20, 15, 0, TimeSpan.Zero);

        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(Day, 100m, same, ingestId: "run-a"),
            QueryFixture.Bar(Day, 101m, same, ingestId: "run-b"));

        var first = await Read(DateTimeOffset.UtcNow);
        var second = await Read(DateTimeOffset.UtcNow);

        // run-b wins on IngestId DESC, and must do so on every run.
        first.Single().Close.ShouldBe(101m);
        second.Single().Close.ShouldBe(first.Single().Close);
    }

    [Fact]
    public async Task T7_rebuild_determinism()
    {
        // Normalising the same raw twice must yield an identical row set. Asserted on rows
        // read back, not on file bytes, because Parquet embeds a writer version and its
        // bytes depend on row-group boundaries.
        var observed = new DateTimeOffset(2024, 6, 14, 20, 15, 0, TimeSpan.Zero);

        await _fixture.WriteBarsAsync(QueryFixture.Bar(Day, 100m, observed, ingestId: "run-1"));
        var first = await Read(DateTimeOffset.UtcNow);

        await _fixture.WriteBarsAsync(QueryFixture.Bar(Day, 100m, observed, ingestId: "run-2"));
        var second = await Read(DateTimeOffset.UtcNow);

        second.Count.ShouldBe(first.Count);
        second.Single().Close.ShouldBe(first.Single().Close);
        second.Single().ObservedAt.ShouldBe(first.Single().ObservedAt);
        second.Single().ObservedAtKind.ShouldBe(first.Single().ObservedAtKind);
    }

    [Fact]
    public async Task T8_observed_only_excludes_inferred()
    {
        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(Day, 100m, new DateTimeOffset(2024, 6, 14, 20, 15, 0, TimeSpan.Zero), ObservationKind.Inferred),
            QueryFixture.Bar(new DateOnly(2024, 6, 17), 102m, new DateTimeOffset(2024, 6, 17, 20, 15, 0, TimeSpan.Zero), ObservationKind.Observed));

        var all = await Read(DateTimeOffset.UtcNow, mode: ObservationMode.All);
        var observedOnly = await Read(DateTimeOffset.UtcNow, mode: ObservationMode.ObservedOnly);

        all.Count.ShouldBe(2);
        observedOnly.ShouldHaveSingleItem().EffectiveDate.ShouldBe(new DateOnly(2024, 6, 17));
    }

    [Fact]
    public async Task Observed_only_does_not_change_the_adjustment_factor()
    {
        await _fixture.WriteBarsAsync(
            QueryFixture.Bar(Day, 400m, new DateTimeOffset(2024, 6, 14, 20, 15, 0, TimeSpan.Zero), ObservationKind.Observed));
        // The split is INFERRED, as every backfilled action is.
        await _fixture.WriteActionsAsync(
            QueryFixture.Split(new DateOnly(2024, 8, 30), 0.25m,
                new DateTimeOffset(2024, 8, 30, 20, 15, 0, TimeSpan.Zero)));

        var bars = await Read(DateTimeOffset.UtcNow, PriceAdjustment.SplitsOnly, ObservationMode.ObservedOnly);

        // If ObservedOnly filtered actions, this would wrongly be 400.
        bars.Single().Close.ShouldBe(100m);
    }

    public void Dispose() => _fixture.Dispose();
}
