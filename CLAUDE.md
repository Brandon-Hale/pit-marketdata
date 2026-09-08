# pit-marketdata — context

A point-in-time market data warehouse for US equities. It answers one question
correctly: **what was knowable about instrument X on date D?**

## Current state

**Stages 0-4 are complete.** Work is on branch `stage4-asof-query`, CI green.

**103 tests, all passing in CI** (which has Docker, so the LocalStack tests run for
real). Locally without Docker: 93 pass, 10 skip.

| Stage | | Status |
|---|---|---|
| 0 | Repo, build baseline, CI | Done |
| 1 | Terraform, **applied to AWS** | Done |
| 2 | Domain facts, clock, stores, Parquet, DuckDB | Done |
| 3 | Vendor integration, parsing, ingest orchestration | Done |
| 4 | As-of query, adjustment, the **eight temporal tests** | Done |
| 5 | Lambda, EventBridge schedule, IAM, CLI | **Next** |
| 6 | Reprocess, compaction, rebuild-from-raw proof | Not started |

There is still **no runnable application**. Everything under `app/src` is a class library;
Stage 5 adds the Lambda and the CLI. Stage 5 has no spec or plan yet.

### Live AWS resources (region ap-southeast-2 = Sydney)

- `pit-marketdata-tfstate-bzun6w` - Terraform state, versioned
- `pit-marketdata-data-bzun6w` - holds `raw/` and `curated/`, currently empty
- `pit-marketdata-marketdata` - DynamoDB, PAY_PER_REQUEST, PITR on
- Two billing alarms in **us-east-1** at USD 5 and 20, SNS subscription confirmed
- `/pit-marketdata/twelvedata/apikey` in SSM as a `SecureString`

Billing alarms must live in us-east-1: AWS publishes `EstimatedCharges` only there, and only
in USD. There is no AUD series, so an AUD alarm would silently never fire. Running cost is
effectively $0/month - every line sits inside a permanent free allowance.

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
  (27 pass, 9 skip). They run for real in CI and on the owner's Mac mini. A
  failure *after* the container starts is a genuine bug and fails loudly by
  design - it is not a missing daemon.
- **AWS CLI** is a user-scope install at
  `%LOCALAPPDATA%\Programs\Amazon\AWSCLIV2\aws.exe`, which may not be on a
  stale inherited PATH. Credentials are configured for `ap-southeast-2`.
- **Twelve Data API key** is at `~/.pit-marketdata-key`. All three endpoints
  (`time_series`, `splits`, `dividends`) were verified working on the real free
  tier on 2026-09-08, so the `MANUAL` corporate-action fallback is not needed.
- `gh` **has** the `workflow` scope. (An earlier note here said otherwise.)

## Vendor payload shapes, confirmed against the live API

- **Prices** are exact decimal *strings* (`"328.31000"`); volume is a string too.
- **Splits** use `date`, and `ratio` is a rounded JSON number - a 7-for-1 split
  reports `0.14286`. Parse `from_factor`/`to_factor` (exact integers) instead and
  derive the ratio, or the adjustment maths inherits the rounding error.
- **Dividends** use `ex_date` and a numeric `amount`.

---

Design decisions were worked through in [this session](https://claude.ai/code/session_01PQ85wGEVmnSeAVnjeC9xta).
