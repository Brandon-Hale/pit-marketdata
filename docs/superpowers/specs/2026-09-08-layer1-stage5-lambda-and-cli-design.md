# Layer 1 Stage 5 — The scheduled Lambda and the CLI

**Status:** design, approved 2026-09-08. Extends
[the Layer 1 design](2026-09-08-layer1-point-in-time-market-data-design.md) §4 with the
detail it left open, and builds on
[the Stage 4 design](2026-09-08-layer1-stage4-asof-query-design.md).

**Goal:** turn a set of libraries into something that runs. A scheduled Lambda keeps the
warehouse current without anyone watching, and a CLI lets a human add symbols, backfill
history, and ask the as-of question by hand.

**Prerequisite state:** Stages 0–4 are complete. 105 tests green. Infrastructure is applied
and the data bucket is still empty — the first real data arrives when this stage runs.

---

## 1. Why this stage exists

Everything built so far is a class library. There is no way to add a symbol, no way to run an
ingest, and no way to see an answer. Stage 4 proved the temporal model is correct; Stage 5 is
what makes it reachable.

It is also the first stage that puts real data in the bucket, which changes the cost of
mistakes. Up to now a bad decision could be fixed by deleting an empty prefix. From here,
`raw/` is a permanent, versioned record — so the ingest path must be right before it runs at
scale, not after.

## 2. Two entry points, one core

```
CLI (local, on demand)                 EventBridge (weekdays 22:15 UTC)
  watchlist add AAPL                              |
  backfill AAPL --from 2015-01-01                 v
  query AAPL --on D --as-of T           Ingest Lambda (.NET 10, ARM64, RC=1)
        |                                 read watchlist as of now
        |                                 per symbol, incremental from cursor
        v                                 write raw/ + curated/, update cursor
  MarketData.Hosting  <---------------->  write a RUN# record
   shared composition
```

The two differ in exactly one way: **the CLI carries DuckDB, the Lambda does not.** Everything
else — S3 clients, stores, repositories, the price source, `IngestService` — is identical, and
lives in `MarketData.Hosting` so it cannot drift. A Lambda writing to a different bucket than
the CLI reads would look like missing data rather than a bug, which is precisely the class of
error worth designing out.

## 3. Prior knowledge without DuckDB

Stage 4 replaced the cursor-watermark guess with a real per-date lookup through the query
layer. That lookup needs DuckDB: a 60 MB native library for `linux-arm64`, plus an `httpfs`
extension that `INSTALL` fetches from `extensions.duckdb.org` at cold start — a third-party
network call on a read-only filesystem.

Rather than ship that, the cursor carries the answer for the only window a daily run touches.

```csharp
public sealed record Cursor(
    string Dataset,
    string Symbol,
    DateOnly? LastEffectiveDate,
    string? LastContentHash,
    IReadOnlyDictionary<DateOnly, decimal>? RecentCloses = null);
```

```
pk: CURSOR#prices_daily
sk: SYMBOL#AAPL
    last_effective_date: 2026-09-05
    last_content_hash:   sha256:9f2a...
    recent_closes:
      "2026-09-01": 231.40
      "2026-09-02": 229.85
      ...  newest 10 kept, older dropped on save
```

`IngestService` already takes prior knowledge as a delegate. The CLI passes the DuckDB-backed
one from `PriorKnowledge.From`; the Lambda passes one that reads `RecentCloses`. `IngestService`
neither knows nor cares which it received.

**Why ten is enough, and where it is not.** A scheduled run fetches from the cursor date
forward — one or two trading days, occasionally a few more after a holiday or an outage. Ten
days of closes covers that with room to spare. It does **not** cover a restatement of a bar
from six months ago: the Lambda would see that date as unknown and stamp it `Inferred`, which
is wrong. This is a real and deliberate limitation, and the mitigation is that deep restatements
are found by `reprocess` in Stage 6, running in the CLI with the full query layer. It is
recorded in §9 rather than left to be discovered.

## 4. The Lambda

A plain handler on `Amazon.Lambda.RuntimeSupport` 2.2.0. No Annotations framework: there is
one entry point, and source generation would hide the only interesting code in the project.

```
1. read the watchlist as of now
2. for each symbol, in order:
     IngestService.IngestSymbolAsync(symbol, from: cursor + 1, to: today)
3. write a RUN# record
```

Symbols are processed **sequentially**, not in parallel. At twenty symbols against a
8-credits-per-minute vendor limit, concurrency buys nothing and risks a 429 that would look
like missing data.

A failure on one symbol must not abort the rest: each is wrapped, the error recorded against
that symbol in the run record, and the run continues. A single bad symbol taking down the whole
schedule is a worse failure than a gap in one series.

**Configuration** comes from environment variables set by Terraform — bucket, table, region —
except the vendor key, which is read from SSM at `/pit-marketdata/twelvedata/apikey`. The key
never appears in the function's environment, where it would be visible in the console and in
`GetFunctionConfiguration`.

**Runtime:** ARM64, 512 MB, `dotnet10` managed runtime, reserved concurrency 1, 5 minute
timeout. Reserved concurrency is not an optimisation — two concurrent runs would both write
raw objects and both advance the same cursor.

### Run records

```
pk: RUN#2026-09-08T22:15:03Z
sk: META
    started_at, finished_at, symbols_attempted, symbols_succeeded,
    rows_written, actions_written, skipped_unchanged, errors: [...]
    expires_at: <unix ts, +90 days>
```

`expires_at` uses the TTL attribute the table was provisioned with in Stage 1, so old records
expire without a cleanup job. This is the only visibility into a job nobody watches; a
scheduled process with no record of what it did cannot be trusted, and the cost is one small
write per day.

## 5. The CLI

`System.CommandLine` 2.0.11 — stable as of this design, first-party, and no heavier than
hand-rolling the parsing would be.

```
marketdata watchlist add AAPL
marketdata watchlist remove AAPL
marketdata watchlist list [--as-of 2026-01-01]

marketdata backfill AAPL --from 2015-01-01 [--to 2026-09-08]

marketdata query AAPL --on 2020-06-15 --as-of 2020-07-01
                      [--adjust none|splits|all] [--observed-only]
```

Invoked as `dotnet run --project app/src/MarketData.Cli -- <args>`, with a shell alias for
daily use. No packaging step, so there is no way to accidentally run a stale build.

**`--as-of` is required on `query`.** Omitting it is a usage error, not a default to now. A
convenience default is exactly how lookahead bias would re-enter a system that has spent four
stages designing it out, and the CLI is the most likely place for that convenience to be added
later "just for interactive use".

**Backfill paces itself.** `IPriceSource.Limits` declares 800 credits/day and 8/minute and has
been unused since Stage 3; this is where it earns its place. A backfill of one symbol costs
3 credits, so the limit only binds when adding many symbols at once, but the pacing belongs in
the code rather than in the operator's head.

**Watchlist membership is bitemporal** and already implemented: `add` stamps when, and
`list --as-of` answers what was being tracked at a past date. Without that the universe itself
would not be point-in-time, and every historical query would silently know about symbols that
were not being tracked at the time.

## 6. Shared composition

`MarketData.Hosting` exposes one method:

```csharp
public static IServiceCollection AddMarketData(
    this IServiceCollection services, MarketDataOptions options);
```

registering the S3 and DynamoDB clients, `IRawStore`, `ICuratedStore`, the three repositories,
`IPriceSource`, the normalisers, `ObservationPolicy` and `IngestService`. Each entry point adds
only what is specific to it: the CLI registers `IMarketDataQuery` and the DuckDB-backed prior
knowledge; the Lambda registers the cursor-backed one.

`MarketDataOptions` carries the bucket, table, region and SSM parameter name. Both entry points
resolve it from environment variables, so a local run and a deployed run differ only in the
values.

## 7. Deployment

`scripts/build-lambda.sh` runs `dotnet publish -r linux-arm64 -c Release` and zips the output
to `dist/lambda.zip`. Terraform takes the path as a variable and computes its hash so a
rebuilt zip triggers a new deployment.

Applied by hand, as Stages 1 and 4 were. GitHub OIDC would remove the local step but requires
an identity provider and role trust that the original design deferred to hardening; adding it
here would mean setting up CI deployment credentials before the thing being deployed has ever
run successfully.

**A new Terraform module,** `modules/ingest`: the function, its role and policy, the EventBridge
rule and permission, and a CloudWatch log group with 14-day retention. Log retention is set
explicitly because the default is *never expire*, which is a slow, silent cost.

## 8. Testing

The two entry points are thin, and their logic belongs to code already tested. What is worth
testing here is what is new:

- **`RecentCloses` round-trips** through DynamoDB, and is trimmed to ten newest on save
  (integration, LocalStack).
- **The cursor-backed prior knowledge** returns a close for a date in the map, and null
  outside it — proving the Lambda's answer matches the query layer's for the recent window.
- **A failing symbol does not abort the run**: two symbols, the first throwing, the second
  still ingested and both recorded.
- **The run record is written** with a TTL roughly 90 days out.
- **CLI parsing**: `query` without `--as-of` exits non-zero with a usage error;
  `--adjust` maps to the right `PriceAdjustment`; `watchlist list --as-of` passes the date
  through.
- **The Lambda project has no DuckDB reference** — an architecture test in the same spirit as
  Domain's, because the whole point of §3 is that the package stays small, and a transitive
  reference would undo it silently.

## 9. Known limitations

Recorded so they are decisions rather than discoveries.

- **Ten days of recent closes** bounds what the Lambda can detect. A restatement of an older
  bar is stamped `Inferred` rather than recognised as a correction. `reprocess` in Stage 6
  is the answer; until then, a monthly `backfill` over a wider window through the CLI catches
  them.
- **No alerting on failure.** The run record captures errors, but nothing reads it. A failed
  run is discovered by looking. An SNS notification on the existing billing topic would be a
  small addition and is deliberately out of scope here.
- **Sequential symbols** means run time grows linearly. At twenty symbols and ~1.2 s each this
  is irrelevant; at two hundred it would need revisiting.
- **The vendor key is read per invocation.** One SSM call per run, not cached across cold
  starts. Simpler, and one call a day is free.

## 10. Out of scope

- `reprocess` and `compact` — Stage 6.
- Run records mirrored to S3 — Stage 6.
- GitHub OIDC for deployment — hardening.
- Any hosted API or UI. If one is ever built, `asOf` is a required parameter and a request
  without it is a 400; the web tier gets `s3:GetObject` on `curated/*` and nothing more.
- A second vendor, and the SEC EDGAR join that `cik` exists for.
