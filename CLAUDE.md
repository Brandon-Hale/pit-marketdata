# pit-marketdata — context

A point-in-time market data warehouse for US equities. It answers one question
correctly: **what was knowable about instrument X on date D?**

## Current state

**Stages 0-5 are complete and deployed.** 166 tests: 152 pass, 14 skip (the LocalStack
ones, where Docker is absent). Verified 2026-09-08.

| Stage | | Status |
|---|---|---|
| 0-3 | Repo, Terraform, data layer, vendor integration | Done |
| 4 | As-of query, adjustment, the eight temporal tests | Done |
| 5 | Lambda, EventBridge schedule, IAM, CLI | Done, deployed |
| 6 | Reprocess, compaction, rebuild-from-raw proof | **Next**, no spec yet |

There is **real data in the warehouse**, but only for AAPL: 2,936 rows from 2015 to
2026-09-04, ~1MB across 80 objects (51 `prices_daily` parts, 29 `corporate_actions`).

The **watchlist holds 8 symbols** as at 2026-09-08 — AAPL, AMD, AMZN, GOOGL, NBIS, NVDA,
PLTR, QQQ — so seven of them are tracked with no bars yet. Of those, only QQQ can complete a
scheduled ingest: the rest are 403 on `/splits` and hit the gap described under corporate
actions below.

### Live AWS resources (ap-southeast-2 = Sydney)

- `pit-marketdata-tfstate-bzun6w` / `pit-marketdata-data-bzun6w` - state and data
- `pit-marketdata-marketdata` - DynamoDB: watchlist, cursors, run records
- `pit-marketdata-ingest` - Lambda, managed `dotnet10` runtime, arm64, 512MB, weekdays 22:15 UTC
- `/aws/lambda/pit-marketdata-ingest` - logs, 14 day retention
- Two billing alarms in **us-east-1** at USD 5 and 20
- `/pit-marketdata/twelvedata/apikey` in SSM as a SecureString

### Running it

```bash
export MARKETDATA_DATA_BUCKET=pit-marketdata-data-bzun6w
export MARKETDATA_TABLE_NAME=pit-marketdata-marketdata
dotnet run --project app/src/MarketData.Cli -- query AAPL --on 2020-06-15 --as-of 2020-07-01
```

Deploying the Lambda: `./scripts/build-lambda.sh` then `terraform apply` from
`infra/terraform`.

## Disk space warning

`DuckDB.NET.Data.Full` copies ~315 MB of native binaries for five platforms into the output
of every **executable** project that references it - measured at 327 MB per test project,
but only 68 KB for a library. Three test projects now carry it. Run
`dotnet build-server shutdown` then delete `app/**/bin` and `app/**/obj` to reclaim it. The
NuGet cache is a further 3.5 GB (`dotnet nuget locals all --clear`).

## Start here

1. Read [`docs/superpowers/specs/2026-09-08-layer1-point-in-time-market-data-design.md`](docs/superpowers/specs/2026-09-08-layer1-point-in-time-market-data-design.md) - what and why.
2. Read [`docs/superpowers/plans/2026-09-08-layer1-foundations.md`](docs/superpowers/plans/2026-09-08-layer1-foundations.md) - 19 tasks, Stages 0-3.
3. The next plan covers Stages 4-6 and has not been written yet.

There is deliberately **no runnable application yet**. Everything under `app/src`
is a class library. The Lambda, the CLI and the as-of query layer are Stages 4-6
and get their own plan. Do not build them early.

## Non-negotiables

These are the correctness rules the whole project exists to uphold. Breaking one is silent —
nothing throws, the numbers just become wrong.

- **Two timestamps on every fact.** `effective_date` is what it is *about*; `observed_at` is
  when it was *learned*. Never conflate them.
- **`observed_at` is stamped once, by the fetcher, into the raw envelope.** Normalisers copy
  it. A normaliser that reads a clock breaks rebuild determinism.
- **Parsing is a pure function of the raw envelope** — no clock, no I/O, no prior state. The
  timestamp decision lives separately in `ObservationPolicy`.
- **Append-only.** No code path updates or deletes a curated row. A restatement is a new row.
- **Prices are stored unadjusted**, parsed to `decimal` from the vendor's exact decimal
  strings. Never `double`. Never store vendor-adjusted prices. Twelve Data's
  `/time_series` returns **split-adjusted** prices with no way to disable it, so
  `IngestService` divides by the same-run split factor to recover what was quoted that day,
  and records the splits envelope in `SplitsRawKey` so a rebuild redoes identical
  arithmetic. Ingest divides; query multiplies. A round-trip test guards the pair.
- **`ObservationMode` applies to bars only, never to corporate actions.** Every backfilled
  action is `INFERRED` because its `observed_at` is its ex-date, so filtering actions under
  `ObservedOnly` would find no splits, apply a factor of 1.0, and return unadjusted prices
  labelled as adjusted. Nothing would throw.
- **The Lambda uses the managed `dotnet10` runtime, not a custom one.** It is a library, not
  an executable: no `Main`, no `LambdaBootstrap`, no `Amazon.Lambda.RuntimeSupport`. The
  handler is `MarketData.Lambda::MarketData.Lambda.Function::HandleAsync` and the serializer
  is declared at assembly level. Published package is 6.3MB.
- **The Lambda must never reference DuckDB**, directly or transitively. An architecture test
  enforces it, paired with a second test proving the same walk finds DuckDB in
  MarketData.Query, so a passing guard means something.
- **An empty vendor window is not an error.** Twelve Data answers a date range with no bars
  using HTTP 400 and "No data is available", not an empty 200. Since the cursor sits at the
  latest bar, incremental runs ask for empty windows constantly. `VendorNoDataException`
  turns that into a skip; any other 400 still throws.
- **Prior knowledge is loaded once per symbol, never per date.** Both `PriorKnowledge.From`
  and `CursorPriorKnowledge.From` cache, and both have counting tests. A per-date version
  made an eleven-year backfill run over ten minutes and write nothing.
- **Reserved concurrency is unset** because this account's total Lambda concurrency quota is
  10 and AWS refuses a reservation leaving fewer than 10 unreserved. Set
  `reserved_concurrency = 1` once the quota is raised.
- **Corporate actions are paywalled per symbol, not by a simple AAPL allowlist.** `/splits`
  and `/dividends` return 403 on the free tier for most symbols but not all. Verified
  2026-09-08: **AAPL and QQQ are served; AMD, PLTR, MSFT, NVDA and SPY return 403.** Do not
  assume a symbol works because another does — check it. Since prices are split-adjusted,
  ingesting without a split history would store adjusted values as raw ones. A 403 is
  `VendorNotEntitledException`; `IngestService` falls back to recorded actions when a lookup
  is supplied and fails loudly when one is not. Enter them with `marketdata action add`,
  which writes `source = MANUAL`.
- **The scheduled Lambda cannot use hand-entered actions.** `AddMarketData` builds
  `IngestService` without a `knownActionsLookup`, and the Lambda cannot supply the CLI's
  because that reads through DuckDB, which the Lambda must never reference. So a 403 symbol
  fails every nightly run even after its splits are recorded by hand. `IngestRunner` catches
  it per symbol, so the run survives and the others still ingest — but those symbols only
  advance via `marketdata backfill` from the CLI. Closing this needs the actions read to come
  from somewhere the Lambda can reach.
- **`--as-of` is parsed as UTC** when no offset is given. Local parsing shifted a bare date
  by ten hours in Sydney and silently returned no rows.
- **`asOf` is never optional.** `IMarketDataQuery` has no overload without it, and any
  future API must reject a request that omits it rather than defaulting to now.
- **A restatement never gets an inferred timestamp** — it takes the real fetch time. Inferring
  one would claim a corrected value was knowable at the original date.
- **No `DateTime.UtcNow` / `DateTimeOffset.UtcNow`** outside a composition root. All time comes
  from an injected `TimeProvider` so tests can control it.
- **API keys are redacted from anything written to storage.** Raw storage is permanent and
  versioned; a leaked key is unrecoverable.
- **`MarketData.Domain` has zero package references.** An architecture test enforces it.

## Layout

The .NET solution lives under `app/`, not the repo root, so each toolchain owns
one top-level folder:

```
app/          MarketData.slnx, Directory.*.props, global.json, src/, tests/
infra/        Terraform (bootstrap/, modules/storage, modules/observability)
scripts/      shell helpers
docs/         spec and plans
.editorconfig at the ROOT - it carries Terraform and YAML rules too
```

## Conventions

- .NET 10, `net10.0`, nullable enabled, warnings as errors.
- **Central package management** - all versions pinned in `app/Directory.Packages.props`.
  After any `dotnet add package`, strip the `Version=` attribute it writes.
- Writes use Parquet.Net; **reads use DuckDB, including in tests**.
- Terraform state uses the S3 backend with `use_lockfile = true`. No lock table.
- Small, frequent commits - one per task, as the plan specifies.

## Toolchain facts that contradict the plan

The plan was written before these were known. Do not "fix" the code back.

- **Tests run on Microsoft.Testing.Platform, not VSTest.** The .NET 10 SDK
  refuses the VSTest target outright, so `Microsoft.NET.Test.Sdk` and
  `xunit.runner.visualstudio` are deliberately absent from
  `Directory.Packages.props`. Adding them back breaks `dotnet test`.
- **`dotnet new sln` emits `MarketData.slnx`**, not `.sln`. The architecture test
  accepts either.
- **`.editorconfig` uses `for_non_interface_members`**, not `always`, for
  accessibility modifiers. "always" makes IDE0040 a build error on every
  interface member under TreatWarningsAsErrors.
- **Testcontainers 4.15** takes the image in the `LocalStackBuilder` constructor
  and supplies its own readiness probe. `Build()` probes Docker eagerly and
  throws from a field initializer, so it must be constructed inside a try/catch.
- **DuckDB returns a DATE column as `DateOnly`**, not `DateTime`. The plan says
  otherwise.
- **CI runs the whole suite with no category filter.** Filtering emptied the
  integration assembly, and MTP treats "zero tests ran" as a failure. GitHub's
  ubuntu-latest has Docker, so the LocalStack tests run for real there.

## Environment

- **Docker is not available on the Windows dev machine.** The LocalStack tests
  self-skip when no daemon is reachable, so `dotnet test` stays green locally
  (152 pass, 14 skip). They run for real in CI and on the owner's Mac mini. A
  failure *after* the container starts is a genuine bug and fails loudly by
  design - it is not a missing daemon.
- **AWS CLI** is a user-scope install at
  `%LOCALAPPDATA%\Programs\Amazon\AWSCLIV2\aws.exe`, which may not be on a
  stale inherited PATH. Credentials are configured for `ap-southeast-2`.
- **Twelve Data API key** is at `~/.pit-marketdata-key`. All three endpoints
  (`time_series`, `splits`, `dividends`) work on the free tier **for AAPL**, which is
  the symbol they were first verified against. `splits` and `dividends` are 403 for
  most other symbols, so the `MANUAL` fallback very much is needed - see the
  corporate-actions non-negotiable above.
- `gh` **has** the `workflow` scope. (An earlier note here said otherwise.)
- **A stale `bin`/`obj` makes `dotnet test` report "Zero tests ran" for all eight
  assemblies** (exit code 5), which looks like total collapse and is not. The stale
  test apps are built as xunit's own console runner instead of the MTP entry point,
  so `dotnet test` drives them with MTP arguments they reject. Running the `.exe`
  directly still passes, which is the tell. `dotnet clean` then rebuild fixes it.
  Likely after the disk-space cleanup above, since incremental build does not
  regenerate the entry point. A genuine break would fail differently.
- **Git Bash mangles AWS log group names.** `aws logs tail /aws/lambda/...` fails with
  `InvalidParameterException` because MSYS rewrites the leading `/` into a Windows
  path. Prefix the command with `MSYS_NO_PATHCONV=1`.

## Vendor payload shapes, confirmed against the live API

- **Prices** are exact decimal *strings* (`"328.31000"`); volume is a string too.
- **Splits** use `date`, and `ratio` is a rounded JSON number - a 7-for-1 split
  reports `0.14286`. Parse `from_factor`/`to_factor` (exact integers) instead and
  derive the ratio, or the adjustment maths inherits the rounding error.
- **Dividends** use `ex_date` and a numeric `amount`. They are ingested alongside splits
  but play no part in un-adjusting: the vendor adjusts for splits only, so folding
  dividends into the un-adjust would corrupt every stored price.

---

Design decisions were worked through in [this session](https://claude.ai/code/session_01PQ85wGEVmnSeAVnjeC9xta).
