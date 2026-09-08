# Layer 1 Foundations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the infrastructure, data layer and vendor integration for a point-in-time US-equity market data warehouse, so that daily bars and corporate actions flow from Twelve Data into immutable raw storage and curated Parquet with correct bitemporal timestamps.

**Architecture:** Foundations first. Terraform provisions S3 and DynamoDB before any code touches them. A dependency-free domain project defines the temporal rules; storage abstractions sit over local disk and S3 behind identical interfaces so every test runs locally. Vendor fetch and normalisation are separate interfaces — the fetcher stamps `observed_at` into an immutable raw envelope and the normaliser copies it, which makes normalisation a pure function of raw and the curated store rebuildable.

**Tech Stack:** C# / .NET 10, Parquet.Net 6.1.0, DuckDB.NET 1.5.5, AWS SDK v4, Terraform 1.14 with AWS provider 6.x, xUnit v3, Shouldly, Testcontainers.LocalStack.

**Spec:** `docs/superpowers/specs/2026-09-08-layer1-point-in-time-market-data-design.md`

**Scope:** Stages 0–3 of the spec (repo, infrastructure, data layer, integration). Stages 4–6 — the as-of query layer, adjustment calculation, the eight temporal tests, the Lambda and the CLI — are covered by a separate plan written after this one lands.

## Global Constraints

- **Target framework:** `net10.0`. Lambda uses the managed .NET 10 runtime, ARM64. Native AOT is not used.
- **Region:** `ap-southeast-2`.
- **Nullable reference types enabled; warnings are errors.** No `#pragma warning disable` without a comment explaining why.
- **Central package management.** All versions pinned in `Directory.Packages.props`. No project declares a version inline. No floating versions.
- **`MarketData.Domain` has zero package references.** BCL only. This is enforced by a test in Task 1 and must never be relaxed.
- **No `DateTime.UtcNow` or `DateTimeOffset.UtcNow` anywhere outside a composition root.** All time comes from an injected `TimeProvider`. A temporal system whose tests cannot control the clock cannot be tested.
- **Prices are stored raw and unadjusted**, exactly as the vendor returned them, parsed to `decimal` from the vendor's exact decimal strings. Never `double`.
- **`observed_at` is stamped once, by the fetcher, into the raw envelope.** Normalisers copy it. A normaliser that reads a clock is a bug.
- **Append-only.** No code path updates or deletes a curated row.
- **API keys are redacted from anything written to storage.** Raw storage is permanent and versioned.
- **Reads use DuckDB, writes use Parquet.Net.** Verified 2026-09-08: Parquet.Net 6.1.0's class deserializer returns `DeserializationResult<T>` and fails on plain `string` properties (strings read back as `ReadOnlyMemory<char>`). Tests read Parquet back through DuckDB, which is also the production read path.
- **`DateOnly` is accepted by Parquet.Net on write but normalises to `DateTime` in the read schema.** The query layer always casts explicitly (`CAST(effective_date AS DATE)`) so the mapping is never load-bearing.

## File Structure

```
Directory.Build.props                    shared TFM, nullable, warnings-as-errors
Directory.Packages.props                 all pinned versions
.editorconfig                            style rules enforced in build
MarketData.sln

src/MarketData.Domain/                   ZERO dependencies
  Observation.cs                         ObservationKind, ObservedAt
  DailyBar.cs                            price fact
  CorporateAction.cs                     split/dividend fact
  IPublicationClock.cs                   inferred-timestamp contract
  UsEquityPublicationClock.cs            16:00 America/New_York + 15min

src/MarketData.Storage/
  RawEnvelope.cs                         wrapper written to raw/
  ContentHash.cs                         SHA-256 + key redaction
  IRawStore.cs / ICuratedStore.cs        storage contracts
  Local/LocalRawStore.cs                 filesystem raw
  Local/LocalCuratedStore.cs             filesystem Parquet
  S3/S3RawStore.cs / S3/S3CuratedStore.cs
  Parquet/PriceRow.cs                    persistence DTO (primitives only)
  Parquet/CorporateActionRow.cs
  Dynamo/WatchlistRepository.cs          bitemporal membership
  Dynamo/CursorRepository.cs

src/MarketData.Sources/
  IPriceSource.cs / IPriceNormaliser.cs
  TwelveData/TwelveDataPriceSource.cs
  TwelveData/TwelveDataPriceNormaliser.cs
  TwelveData/TwelveDataActionsNormaliser.cs

tests/MarketData.Domain.Tests/
tests/MarketData.Storage.Tests/
  DuckDbReader.cs                        test-only Parquet read-back helper
tests/MarketData.Integration.Tests/      LocalStack; requires Docker

infra/terraform/
  bootstrap/                             state bucket, local state, run once
  modules/storage/                       S3 data bucket + DynamoDB table
  modules/observability/                 billing alarms, log retention
  main.tf variables.tf outputs.tf backend.tf terraform.tfvars.example
```

---

# Stage 0 — Repository and build baseline

### Task 1: Solution skeleton with pinned packages and an enforced dependency boundary

**Files:**
- Create: `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `MarketData.sln`
- Create: `src/MarketData.Domain/MarketData.Domain.csproj`
- Create: `tests/MarketData.Domain.Tests/MarketData.Domain.Tests.csproj`
- Test: `tests/MarketData.Domain.Tests/ArchitectureTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: a solution where `dotnet build` and `dotnet test` succeed; every later task adds projects to this solution and versions to `Directory.Packages.props`.

- [ ] **Step 1: Create the solution and the two projects**

```bash
cd C:/Users/brand/Documents/Git/pit-marketdata
dotnet new sln -n MarketData
dotnet new classlib -o src/MarketData.Domain -f net10.0
dotnet new xunit3 -o tests/MarketData.Domain.Tests -f net10.0
rm -f src/MarketData.Domain/Class1.cs tests/MarketData.Domain.Tests/UnitTest1.cs
dotnet sln add src/MarketData.Domain tests/MarketData.Domain.Tests
dotnet add tests/MarketData.Domain.Tests reference src/MarketData.Domain
```

If `dotnet new xunit3` is not available, run `dotnet new install xunit.v3.templates` first.

- [ ] **Step 2: Write `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <InvariantGlobalization>false</InvariantGlobalization>
  </PropertyGroup>
</Project>
```

`InvariantGlobalization` must stay `false`: `TimeZoneInfo.FindSystemTimeZoneById("America/New_York")` needs the timezone database, and the inferred-timestamp rule depends on it.

- [ ] **Step 3: Write `Directory.Packages.props`**

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup Label="Storage">
    <PackageVersion Include="Parquet.Net" Version="6.1.0" />
    <PackageVersion Include="AWSSDK.S3" Version="4.0.102.5" />
    <PackageVersion Include="AWSSDK.DynamoDBv2" Version="4.0.103.6" />
    <PackageVersion Include="AWSSDK.SimpleSystemsManagement" Version="4.0.103.7" />
    <PackageVersion Include="AWSSDK.Extensions.NETCore.Setup" Version="4.0.101.2" />
  </ItemGroup>
  <ItemGroup Label="Sources">
    <PackageVersion Include="Microsoft.Extensions.Http.Resilience" Version="10.9.0" />
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.11" />
  </ItemGroup>
  <ItemGroup Label="Query">
    <PackageVersion Include="DuckDB.NET.Data.Full" Version="1.5.5" />
  </ItemGroup>
  <ItemGroup Label="Testing">
    <PackageVersion Include="xunit.v3" Version="4.0.0" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.0" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.9.0" />
    <PackageVersion Include="Shouldly" Version="4.3.0" />
    <PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.9.0" />
    <PackageVersion Include="Testcontainers.LocalStack" Version="4.15.0" />
    <PackageVersion Include="coverlet.collector" Version="10.0.1" />
  </ItemGroup>
</Project>
```

Then remove every `Version=` attribute from the two generated `.csproj` files, leaving bare `<PackageReference Include="..." />`.

If `xunit.runner.visualstudio` 3.1.0 does not resolve, run
`curl -s https://api.nuget.org/v3-flatcontainer/xunit.runner.visualstudio/index.json`
and pin the highest stable version listed.

- [ ] **Step 4: Write `.editorconfig`**

```ini
root = true

[*.cs]
indent_style = space
indent_size = 4
end_of_line = lf
insert_final_newline = true
dotnet_diagnostic.CA1062.severity = none
csharp_style_namespace_declarations = file_scoped:warning
dotnet_style_require_accessibility_modifiers = always:warning

[*.{tf,tfvars}]
indent_size = 2

[*.{json,yml,yaml}]
indent_size = 2
```

- [ ] **Step 5: Write the failing architecture test**

This test is the enforcement mechanism for the spec's "Domain has zero dependencies" rule. It reads the project file directly rather than inspecting assemblies, because a transitive-free assembly can still carry a `PackageReference` that a later task would quietly rely on.

```csharp
namespace MarketData.Domain.Tests;

public sealed class ArchitectureTests
{
    private static string DomainCsprojPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MarketData.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "MarketData.Domain", "MarketData.Domain.csproj");
    }

    [Fact]
    public void Domain_project_has_no_package_references()
    {
        var csproj = File.ReadAllText(DomainCsprojPath());

        Assert.DoesNotContain("PackageReference", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void Domain_project_has_no_project_references()
    {
        var csproj = File.ReadAllText(DomainCsprojPath());

        Assert.DoesNotContain("ProjectReference", csproj, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test`
Expected: PASS — both tests green, because the Domain csproj is currently empty of references. If either fails, a package snuck into Domain; remove it rather than relaxing the test.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Add solution skeleton with centrally pinned packages

Domain is guarded by an architecture test that reads its csproj
directly, so a package reference cannot be added without a
deliberate, visible test change."
```

---

### Task 2: Continuous integration for .NET

**Files:**
- Create: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: the solution from Task 1.
- Produces: a green CI badge; every later task's tests run here on push.

**Prerequisite:** pushing `.github/workflows/` requires the `workflow` OAuth scope. Run `gh auth refresh -h github.com -s workflow` in an interactive terminal and confirm with `gh auth status` before Step 3. Without it the push is rejected by GitHub, not by git.

- [ ] **Step 1: Write the workflow**

```yaml
name: ci

on:
  push:
    branches: [main]
  pull_request:

jobs:
  dotnet:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v5

      - uses: actions/setup-dotnet@v5
        with:
          dotnet-version: '10.0.x'

      - name: Restore
        run: dotnet restore

      - name: Build
        run: dotnet build --no-restore --configuration Release

      - name: Test
        run: dotnet test --no-build --configuration Release --verbosity normal
```

Integration tests requiring Docker are excluded from this job by trait filtering in Task 13; until then there are none.

- [ ] **Step 2: Verify the workflow file parses**

Run: `gh workflow list` after pushing, or lint locally with
`node -e "require('fs').readFileSync('.github/workflows/ci.yml','utf8')"` as a smoke check.
Expected: the file exists and is non-empty. Real validation happens on push.

- [ ] **Step 3: Commit and push, then confirm the run**

```bash
git add .github/workflows/ci.yml
git commit -m "Add .NET CI workflow"
git push
gh run list --limit 1
```

Expected: one run, concluding `success`. If the push is rejected mentioning `workflow` scope, complete the prerequisite above and push again.

---

# Stage 1 — Infrastructure

Terraform is applied before any code touches AWS. Every resource in this stage is created once and rarely changed.

### Task 3: Terraform state bucket (bootstrap)

**Files:**
- Create: `infra/terraform/bootstrap/main.tf`, `infra/terraform/bootstrap/variables.tf`, `infra/terraform/bootstrap/outputs.tf`, `infra/terraform/bootstrap/README.md`

**Interfaces:**
- Consumes: nothing.
- Produces: an S3 bucket named `${var.project}-tfstate-${var.account_suffix}`, consumed as the `bucket` value in `backend.tf` in Task 6.

This configuration keeps **local state** and is applied exactly once. It cannot use a remote backend because it is what creates the remote backend.

- [ ] **Step 1: Write `bootstrap/variables.tf`**

```hcl
variable "project" {
  description = "Project name prefix for all resources."
  type        = string
  default     = "pit-marketdata"
}

variable "region" {
  description = "AWS region."
  type        = string
  default     = "ap-southeast-2"
}

variable "account_suffix" {
  description = "Short unique suffix making the state bucket name globally unique."
  type        = string
}
```

- [ ] **Step 2: Write `bootstrap/main.tf`**

```hcl
terraform {
  required_version = ">= 1.14"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }
}

provider "aws" {
  region = var.region
}

resource "aws_s3_bucket" "state" {
  bucket = "${var.project}-tfstate-${var.account_suffix}"

  lifecycle {
    prevent_destroy = true
  }
}

resource "aws_s3_bucket_versioning" "state" {
  bucket = aws_s3_bucket.state.id

  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "state" {
  bucket = aws_s3_bucket.state.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
  }
}

resource "aws_s3_bucket_public_access_block" "state" {
  bucket                  = aws_s3_bucket.state.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}
```

`prevent_destroy` is deliberate: destroying the state bucket orphans every other resource.

- [ ] **Step 3: Write `bootstrap/outputs.tf`**

```hcl
output "state_bucket" {
  description = "Name to use as the S3 backend bucket in the root configuration."
  value       = aws_s3_bucket.state.id
}
```

- [ ] **Step 4: Write `bootstrap/README.md`**

```markdown
# Terraform bootstrap

Creates the S3 bucket that holds Terraform state for the root configuration.
Applied **once**, by hand, with local state.

```bash
cd infra/terraform/bootstrap
terraform init
terraform apply -var="account_suffix=<something-short-and-unique>"
```

Copy the `state_bucket` output into `infra/terraform/backend.tf`.

The local `terraform.tfstate` produced here is gitignored. Losing it is
recoverable — the bucket can be re-imported with `terraform import` — but keep it.
```

- [ ] **Step 5: Validate**

Run:
```bash
cd infra/terraform/bootstrap
terraform init
terraform fmt -check
terraform validate
```
Expected: `Success! The configuration is valid.` Do not apply yet — applying is Step 6 and needs AWS credentials.

- [ ] **Step 6: Apply and record the bucket name**

Run: `terraform apply -var="account_suffix=<unique>"`
Expected: one bucket created; `state_bucket` printed. Record the value — Task 6 needs it.

- [ ] **Step 7: Commit**

```bash
git add infra/terraform/bootstrap
git commit -m "Add Terraform bootstrap for remote state bucket

Applied once with local state, since this is the configuration that
creates the backend everything else uses."
```

---

### Task 4: Storage module — S3 data bucket and DynamoDB table

**Files:**
- Create: `infra/terraform/modules/storage/main.tf`, `variables.tf`, `outputs.tf`

**Interfaces:**
- Consumes: `project`, `region` variables.
- Produces: outputs `data_bucket` (string) and `table_name` (string), consumed by the root configuration in Task 6 and by application configuration in Task 13.

- [ ] **Step 1: Write `modules/storage/variables.tf`**

```hcl
variable "project" {
  description = "Project name prefix."
  type        = string
}

variable "bucket_suffix" {
  description = "Short unique suffix making the data bucket name globally unique."
  type        = string
}

variable "raw_ia_transition_days" {
  description = "Days before raw objects move to Infrequent Access."
  type        = number
  default     = 90
}
```

- [ ] **Step 2: Write `modules/storage/main.tf`**

```hcl
resource "aws_s3_bucket" "data" {
  bucket = "${var.project}-data-${var.bucket_suffix}"

  lifecycle {
    prevent_destroy = true
  }
}

resource "aws_s3_bucket_versioning" "data" {
  bucket = aws_s3_bucket.data.id

  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "data" {
  bucket = aws_s3_bucket.data.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
  }
}

resource "aws_s3_bucket_public_access_block" "data" {
  bucket                  = aws_s3_bucket.data.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

# raw/ is the permanent store of record. It is never deleted, only made cheaper.
resource "aws_s3_bucket_lifecycle_configuration" "data" {
  bucket = aws_s3_bucket.data.id

  rule {
    id     = "raw-to-ia"
    status = "Enabled"

    filter {
      prefix = "raw/"
    }

    transition {
      days          = var.raw_ia_transition_days
      storage_class = "STANDARD_IA"
    }
  }
}

resource "aws_dynamodb_table" "marketdata" {
  name         = "${var.project}-marketdata"
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "pk"
  range_key    = "sk"

  attribute {
    name = "pk"
    type = "S"
  }

  attribute {
    name = "sk"
    type = "S"
  }

  ttl {
    attribute_name = "expires_at"
    enabled        = true
  }

  point_in_time_recovery {
    enabled = true
  }

  lifecycle {
    prevent_destroy = true
  }
}
```

`PAY_PER_REQUEST` keeps this inside the free tier at this volume and needs no capacity tuning. The `expires_at` TTL attribute is what expires `RUN#` items after 90 days; it is set by the application, not here.

- [ ] **Step 3: Write `modules/storage/outputs.tf`**

```hcl
output "data_bucket" {
  description = "Name of the S3 bucket holding raw/ and curated/."
  value       = aws_s3_bucket.data.id
}

output "data_bucket_arn" {
  description = "ARN of the data bucket, for IAM policies."
  value       = aws_s3_bucket.data.arn
}

output "table_name" {
  description = "Name of the DynamoDB state table."
  value       = aws_dynamodb_table.marketdata.name
}

output "table_arn" {
  description = "ARN of the DynamoDB table, for IAM policies."
  value       = aws_dynamodb_table.marketdata.arn
}
```

- [ ] **Step 4: Validate**

Run: `cd infra/terraform && terraform fmt -check -recursive && terraform init -backend=false && terraform validate`
Expected: valid. The module is not yet referenced, so `init -backend=false` is enough here.

- [ ] **Step 5: Commit**

```bash
git add infra/terraform/modules/storage
git commit -m "Add Terraform storage module for S3 data bucket and DynamoDB table"
```

---

### Task 5: Observability module — billing alarms and log retention

**Files:**
- Create: `infra/terraform/modules/observability/main.tf`, `variables.tf`, `outputs.tf`

**Interfaces:**
- Consumes: `project`, `alarm_email`, `thresholds`.
- Produces: output `alarm_topic_arn` (string), consumed by the root configuration in Task 6.

Billing metrics live **only** in `us-east-1` regardless of where resources are. This module therefore declares its own aliased provider.

- [ ] **Step 1: Write `modules/observability/variables.tf`**

```hcl
variable "project" {
  description = "Project name prefix."
  type        = string
}

variable "alarm_email" {
  description = "Address to receive billing alarm notifications."
  type        = string
}

variable "billing_thresholds_usd" {
  description = "Estimated-charge thresholds, in USD, that raise an alarm."
  type        = list(number)
  default     = [5, 20]
}
```

- [ ] **Step 2: Write `modules/observability/main.tf`**

```hcl
terraform {
  required_providers {
    aws = {
      source                = "hashicorp/aws"
      configuration_aliases = [aws.us_east_1]
    }
  }
}

resource "aws_sns_topic" "alarms" {
  provider = aws.us_east_1
  name     = "${var.project}-billing-alarms"
}

resource "aws_sns_topic_subscription" "email" {
  provider  = aws.us_east_1
  topic_arn = aws_sns_topic.alarms.arn
  protocol  = "email"
  endpoint  = var.alarm_email
}

resource "aws_cloudwatch_metric_alarm" "billing" {
  provider = aws.us_east_1
  count    = length(var.billing_thresholds_usd)

  alarm_name          = "${var.project}-billing-over-${var.billing_thresholds_usd[count.index]}-usd"
  alarm_description   = "Estimated AWS charges exceeded USD ${var.billing_thresholds_usd[count.index]}."
  namespace           = "AWS/Billing"
  metric_name         = "EstimatedCharges"
  statistic           = "Maximum"
  period              = 21600
  evaluation_periods  = 1
  threshold           = var.billing_thresholds_usd[count.index]
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"

  dimensions = {
    Currency = "USD"
  }

  alarm_actions = [aws_sns_topic.alarms.arn]
}
```

- [ ] **Step 3: Write `modules/observability/outputs.tf`**

```hcl
output "alarm_topic_arn" {
  description = "SNS topic receiving billing alarms."
  value       = aws_sns_topic.alarms.arn
}
```

- [ ] **Step 4: Validate**

Run: `cd infra/terraform && terraform fmt -check -recursive && terraform validate`
Expected: valid.

- [ ] **Step 5: Commit**

```bash
git add infra/terraform/modules/observability
git commit -m "Add Terraform observability module for billing alarms

Billing metrics are only published in us-east-1, so the module takes an
aliased provider rather than assuming the project region."
```

---

### Task 6: Root configuration, remote backend, and Terraform CI

**Files:**
- Create: `infra/terraform/main.tf`, `variables.tf`, `outputs.tf`, `backend.tf`, `terraform.tfvars.example`
- Create: `.github/workflows/terraform.yml`

**Interfaces:**
- Consumes: `modules/storage` and `modules/observability` from Tasks 4 and 5; the state bucket name from Task 3.
- Produces: applied infrastructure, and the outputs `data_bucket` and `table_name` that Task 13 reads to configure the application.

- [ ] **Step 1: Write `backend.tf`**

Replace `REPLACE_ME` with the `state_bucket` value recorded in Task 3, Step 6.

```hcl
terraform {
  backend "s3" {
    bucket       = "REPLACE_ME"
    key          = "layer1/terraform.tfstate"
    region       = "ap-southeast-2"
    encrypt      = true
    use_lockfile = true
  }
}
```

`use_lockfile` is S3-native state locking. DynamoDB-based locking is deprecated, so no lock table exists.

- [ ] **Step 2: Write `variables.tf`**

```hcl
variable "project" {
  description = "Project name prefix for all resources."
  type        = string
  default     = "pit-marketdata"
}

variable "region" {
  description = "AWS region for project resources."
  type        = string
  default     = "ap-southeast-2"
}

variable "bucket_suffix" {
  description = "Short unique suffix making the data bucket name globally unique."
  type        = string
}

variable "alarm_email" {
  description = "Address to receive billing alarm notifications."
  type        = string
}
```

- [ ] **Step 3: Write `main.tf`**

```hcl
terraform {
  required_version = ">= 1.14"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }
}

provider "aws" {
  region = var.region

  default_tags {
    tags = {
      Project   = var.project
      ManagedBy = "terraform"
    }
  }
}

provider "aws" {
  alias  = "us_east_1"
  region = "us-east-1"

  default_tags {
    tags = {
      Project   = var.project
      ManagedBy = "terraform"
    }
  }
}

module "storage" {
  source = "./modules/storage"

  project       = var.project
  bucket_suffix = var.bucket_suffix
}

module "observability" {
  source = "./modules/observability"

  providers = {
    aws.us_east_1 = aws.us_east_1
  }

  project     = var.project
  alarm_email = var.alarm_email
}
```

- [ ] **Step 4: Write `outputs.tf` and `terraform.tfvars.example`**

```hcl
output "data_bucket" {
  description = "S3 bucket holding raw/ and curated/."
  value       = module.storage.data_bucket
}

output "table_name" {
  description = "DynamoDB state table."
  value       = module.storage.table_name
}
```

```hcl
# terraform.tfvars.example — copy to terraform.tfvars (gitignored) and fill in.
bucket_suffix = "ab12cd"
alarm_email   = "you@example.com"
```

- [ ] **Step 5: Validate, plan, and apply**

Run:
```bash
cd infra/terraform
terraform init
terraform fmt -check -recursive
terraform validate
terraform plan
terraform apply
```
Expected: the plan creates one S3 bucket, one DynamoDB table, one SNS topic, one subscription and two billing alarms. Confirm the SNS subscription email — alarms do not fire until it is confirmed.

- [ ] **Step 6: Verify the alarms exist**

Run: `aws cloudwatch describe-alarms --region us-east-1 --alarm-name-prefix pit-marketdata --query 'MetricAlarms[].AlarmName'`
Expected: two alarm names listed. This is the spec's acceptance criterion that billing alarms exist.

- [ ] **Step 7: Write `.github/workflows/terraform.yml`**

```yaml
name: terraform

on:
  push:
    branches: [main]
    paths: ['infra/terraform/**']
  pull_request:
    paths: ['infra/terraform/**']

jobs:
  validate:
    runs-on: ubuntu-latest
    defaults:
      run:
        working-directory: infra/terraform
    steps:
      - uses: actions/checkout@v5

      - uses: hashicorp/setup-terraform@v3
        with:
          terraform_version: 1.14.8

      - name: Format check
        run: terraform fmt -check -recursive

      - name: Validate
        run: terraform init -backend=false && terraform validate
```

`-backend=false` means this job needs no AWS credentials. Planning against real state requires OIDC, which is deliberately deferred to the hardening stage.

- [ ] **Step 8: Commit and push**

```bash
git add infra/terraform .github/workflows/terraform.yml
git commit -m "Add root Terraform configuration with S3-native state locking

Validation runs in CI without credentials; planning against real state
waits for OIDC in the hardening stage."
git push
gh run list --limit 2
```

Expected: both workflows conclude `success`.
---

# Stage 2 — Data layer

The temporal rules are fixed here. They are not a feature of the read path; they are the schema.

### Task 7: Domain facts with a UTC invariant

**Files:**
- Create: `src/MarketData.Domain/Observation.cs`, `src/MarketData.Domain/DailyBar.cs`, `src/MarketData.Domain/CorporateAction.cs`
- Test: `tests/MarketData.Domain.Tests/DailyBarTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `ObservationKind` (enum: `Observed`, `Inferred`); `DailyBar` and `CorporateAction` records with the exact property names and types below. Every later task constructs these.

- [ ] **Step 1: Write the failing tests**

`ObservedAt` carrying a non-UTC offset is the one invariant worth enforcing in the type: an as-of comparison against a value with a hidden offset silently returns the wrong rows.

```csharp
using MarketData.Domain;
using Shouldly;

namespace MarketData.Domain.Tests;

public sealed class DailyBarTests
{
    private static DailyBar Bar(DateTimeOffset observedAt) => new(
        Symbol: "AAPL",
        EffectiveDate: new DateOnly(2020, 8, 31),
        Open: 127.58m, High: 131.00m, Low: 126.00m, Close: 129.04m,
        Volume: 225_702_700L,
        Currency: "USD",
        Source: "twelvedata",
        ObservedAt: observedAt,
        ObservedAtKind: ObservationKind.Inferred,
        IngestId: "run-1",
        RawKey: "raw/x.json");

    [Fact]
    public void Accepts_a_utc_observed_at()
    {
        var bar = Bar(new DateTimeOffset(2020, 8, 31, 20, 15, 0, TimeSpan.Zero));

        bar.ObservedAt.Offset.ShouldBe(TimeSpan.Zero);
        bar.Close.ShouldBe(129.04m);
    }

    [Fact]
    public void Rejects_a_non_utc_observed_at()
    {
        var sydney = new DateTimeOffset(2020, 8, 31, 20, 15, 0, TimeSpan.FromHours(10));

        Should.Throw<ArgumentException>(() => Bar(sydney));
    }

    [Fact]
    public void Preserves_decimal_precision_exactly()
    {
        var bar = Bar(DateTimeOffset.UnixEpoch) with { Close = 328.31000m };

        bar.Close.ToString().ShouldBe("328.31000");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/MarketData.Domain.Tests`
Expected: FAIL — `DailyBar` does not exist.

- [ ] **Step 3: Write `Observation.cs`**

```csharp
namespace MarketData.Domain;

/// <summary>
/// Whether an <c>observed_at</c> instant was recorded live or reconstructed.
/// </summary>
public enum ObservationKind
{
    /// <summary>Captured live: the instant is the real fetch time.</summary>
    Observed,

    /// <summary>Backfilled: the instant is a reconstructed publication time.</summary>
    Inferred
}
```

- [ ] **Step 4: Write `DailyBar.cs`**

```csharp
namespace MarketData.Domain;

/// <summary>
/// One unadjusted daily bar, exactly as a vendor reported it, carrying both the
/// date it is about (<see cref="EffectiveDate"/>) and the instant it was learned
/// (<see cref="ObservedAt"/>).
/// </summary>
public sealed record DailyBar(
    string Symbol,
    DateOnly EffectiveDate,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    string Currency,
    string Source,
    DateTimeOffset ObservedAt,
    ObservationKind ObservedAtKind,
    string IngestId,
    string RawKey)
{
    private readonly DateTimeOffset _observedAt = RequireUtc(ObservedAt);

    /// <summary>The UTC instant this fact was learned. Always zero-offset.</summary>
    public DateTimeOffset ObservedAt
    {
        get => _observedAt;
        init => _observedAt = RequireUtc(value);
    }

    internal static DateTimeOffset RequireUtc(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero
            ? value
            : throw new ArgumentException(
                $"ObservedAt must be UTC (zero offset) but had offset {value.Offset}. " +
                "A non-UTC instant makes as-of comparisons silently wrong.",
                nameof(value));
}
```

- [ ] **Step 5: Write `CorporateAction.cs`**

```csharp
namespace MarketData.Domain;

/// <summary>Kind of corporate action. A reverse split is a <see cref="Split"/> with ratio &gt; 1.</summary>
public enum CorporateActionType
{
    Split,
    Dividend
}

/// <summary>
/// One corporate action. <see cref="Ratio"/> is populated for splits and
/// <see cref="Amount"/> for dividends; the other is null.
/// </summary>
public sealed record CorporateAction(
    string Symbol,
    DateOnly ExDate,
    CorporateActionType ActionType,
    decimal? Ratio,
    decimal? Amount,
    string Currency,
    string Source,
    DateTimeOffset ObservedAt,
    ObservationKind ObservedAtKind,
    string IngestId,
    string RawKey)
{
    private readonly DateTimeOffset _observedAt = DailyBar.RequireUtc(ObservedAt);

    /// <summary>The UTC instant this fact was learned. Always zero-offset.</summary>
    public DateTimeOffset ObservedAt
    {
        get => _observedAt;
        init => _observedAt = DailyBar.RequireUtc(value);
    }
}
```

- [ ] **Step 6: Add Shouldly to the test project and run**

```bash
dotnet add tests/MarketData.Domain.Tests package Shouldly
```
Remove the `Version=` attribute it writes, since versions live in `Directory.Packages.props`.

Run: `dotnet test tests/MarketData.Domain.Tests`
Expected: PASS — 3 tests. The architecture tests from Task 1 must still pass; Domain gained no packages.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Add domain facts with a UTC invariant on observed_at

A non-UTC offset makes as-of comparisons silently wrong, so the type
refuses it rather than trusting every call site."
```

---

### Task 8: The inferred publication clock

**Files:**
- Create: `src/MarketData.Domain/IPublicationClock.cs`, `src/MarketData.Domain/UsEquityPublicationClock.cs`
- Test: `tests/MarketData.Domain.Tests/UsEquityPublicationClockTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `IPublicationClock.InferredPublication(DateOnly) -> DateTimeOffset`. The normaliser in Task 17 calls this for every backfilled bar.

This implements the spec's backfill timestamp rule. Getting daylight saving wrong here shifts every inferred timestamp by an hour twice a year, so the tests below straddle both sides of a DST boundary.

- [ ] **Step 1: Write the failing tests**

```csharp
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
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/MarketData.Domain.Tests --filter UsEquityPublicationClockTests`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Write `IPublicationClock.cs`**

```csharp
namespace MarketData.Domain;

/// <summary>
/// Reconstructs when a daily bar for a given trading date became public.
/// Used only for backfilled history, which has no true observation instant.
/// </summary>
public interface IPublicationClock
{
    /// <summary>The UTC instant a bar for <paramref name="effectiveDate"/> became knowable.</summary>
    DateTimeOffset InferredPublication(DateOnly effectiveDate);
}
```

- [ ] **Step 4: Write `UsEquityPublicationClock.cs`**

```csharp
namespace MarketData.Domain;

/// <summary>
/// US equity publication clock: the regular session closes at 16:00
/// America/New_York, and a daily bar is public shortly after.
/// </summary>
public sealed class UsEquityPublicationClock : IPublicationClock
{
    private static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>16:00 close plus a 15 minute settle-and-publish lag.</summary>
    private static readonly TimeSpan CloseWithLag = new(16, 15, 0);

    public DateTimeOffset InferredPublication(DateOnly effectiveDate)
    {
        var local = effectiveDate.ToDateTime(TimeOnly.MinValue).Add(CloseWithLag);

        // 16:15 never falls inside a US DST transition window (transitions occur
        // at 02:00 local), so the offset is unambiguous and no adjustment is needed.
        var offset = Eastern.GetUtcOffset(local);

        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/MarketData.Domain.Tests --filter UsEquityPublicationClockTests`
Expected: PASS — 4 tests.

If `FindSystemTimeZoneById` throws `TimeZoneNotFoundException`, `InvariantGlobalization` was left `true` in `Directory.Build.props`. Fix that rather than hardcoding offsets.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add US equity publication clock for inferred observed_at

Tests straddle a DST boundary: getting this wrong shifts every
backfilled timestamp by an hour for half the year."
```

---

### Task 9: Raw envelope, content hashing and API key redaction

**Files:**
- Create: `src/MarketData.Storage/MarketData.Storage.csproj`, `src/MarketData.Storage/RawEnvelope.cs`, `src/MarketData.Storage/ContentHash.cs`, `src/MarketData.Storage/UrlRedactor.cs`
- Create: `tests/MarketData.Storage.Tests/MarketData.Storage.Tests.csproj`
- Test: `tests/MarketData.Storage.Tests/UrlRedactorTests.cs`, `tests/MarketData.Storage.Tests/ContentHashTests.cs`

**Interfaces:**
- Consumes: `MarketData.Domain`.
- Produces: `RawEnvelope` record; `ContentHash.Sha256(string) -> string`; `UrlRedactor.Redact(string) -> string`. Task 16 builds envelopes, Task 17 reads them.

`Payload` is the vendor's response as a **verbatim string**, not a parsed object. Storing the exact text is what makes the content hash meaningful and the rebuild faithful.

- [ ] **Step 1: Create the projects**

```bash
dotnet new classlib -o src/MarketData.Storage -f net10.0
dotnet new xunit3 -o tests/MarketData.Storage.Tests -f net10.0
rm -f src/MarketData.Storage/Class1.cs tests/MarketData.Storage.Tests/UnitTest1.cs
dotnet sln add src/MarketData.Storage tests/MarketData.Storage.Tests
dotnet add src/MarketData.Storage reference src/MarketData.Domain
dotnet add tests/MarketData.Storage.Tests reference src/MarketData.Storage
dotnet add tests/MarketData.Storage.Tests package Shouldly
```
Strip the `Version=` attribute from the added `PackageReference`.

- [ ] **Step 2: Write the failing tests**

```csharp
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
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/MarketData.Storage.Tests`
Expected: FAIL — types do not exist.

- [ ] **Step 4: Write `UrlRedactor.cs`**

```csharp
namespace MarketData.Storage;

/// <summary>
/// Removes credentials from URLs before they are written to storage.
/// Raw storage is permanent and versioned, so a leaked key is unrecoverable.
/// </summary>
public static class UrlRedactor
{
    private const string Replacement = "REDACTED";

    private static readonly string[] SensitiveNames =
        ["apikey", "api_key", "token", "key", "secret", "password"];

    public static string Redact(string url)
    {
        var split = url.IndexOf('?');
        if (split < 0)
        {
            return url;
        }

        var prefix = url[..split];
        var pairs = url[(split + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries);

        var redacted = pairs.Select(pair =>
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
            {
                return pair;
            }

            var name = pair[..eq];
            return IsSensitive(name) ? $"{name}={Replacement}" : pair;
        });

        return $"{prefix}?{string.Join('&', redacted)}";
    }

    private static bool IsSensitive(string name) =>
        SensitiveNames.Contains(name, StringComparer.OrdinalIgnoreCase);
}
```

- [ ] **Step 5: Write `ContentHash.cs` and `RawEnvelope.cs`**

```csharp
using System.Security.Cryptography;
using System.Text;

namespace MarketData.Storage;

/// <summary>SHA-256 over a vendor payload, used to short-circuit unchanged fetches.</summary>
public static class ContentHash
{
    public static string Sha256(string payload)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(payload));

        return "sha256:" + Convert.ToHexStringLower(digest);
    }
}
```

```csharp
namespace MarketData.Storage;

/// <summary>
/// Immutable wrapper written to <c>raw/</c>. The fetcher stamps
/// <see cref="ObservedAt"/> here exactly once; normalisers copy it rather than
/// reading a clock, which is what makes normalisation a pure function of raw.
/// </summary>
/// <param name="Payload">
/// The vendor response as verbatim text. Kept as a string, not a parsed object,
/// so the bytes that produced <see cref="ContentHash"/> are exactly what is stored.
/// </param>
public sealed record RawEnvelope(
    string SourceId,
    string Symbol,
    string Dataset,
    DateTimeOffset ObservedAt,
    string RequestUrl,
    string ContentHash,
    string Payload);
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/MarketData.Storage.Tests`
Expected: PASS — 8 tests.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Add raw envelope, content hashing and URL redaction

Payload is kept as verbatim text so the hash covers exactly the bytes
stored. Redaction is tested for idempotence because envelopes get
rewritten during reprocessing."
```

---

### Task 10: Raw store over the local filesystem

**Files:**
- Create: `src/MarketData.Storage/IRawStore.cs`, `src/MarketData.Storage/RawKey.cs`, `src/MarketData.Storage/Local/LocalRawStore.cs`
- Test: `tests/MarketData.Storage.Tests/LocalRawStoreTests.cs`

**Interfaces:**
- Consumes: `RawEnvelope` from Task 9.
- Produces: `IRawStore` with `WriteAsync(RawEnvelope, DateOnly, CancellationToken) -> Task<string>` (returns the key), `ReadAsync(string, CancellationToken) -> Task<RawEnvelope>`, and `ListAsync(string, CancellationToken) -> IAsyncEnumerable<string>`. Task 13 adds the S3 implementation of the same interface; Task 19 writes through it.

- [ ] **Step 1: Write the failing test**

```csharp
using MarketData.Storage;
using MarketData.Storage.Local;
using Shouldly;

namespace MarketData.Storage.Tests;

public sealed class LocalRawStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pitmd-" + Guid.NewGuid().ToString("N"));

    private static RawEnvelope Envelope(string payload = "{\"ok\":true}") => new(
        SourceId: "twelvedata",
        Symbol: "AAPL",
        Dataset: "prices_daily",
        ObservedAt: new DateTimeOffset(2026, 9, 8, 22, 15, 3, TimeSpan.Zero),
        RequestUrl: "https://api.twelvedata.com/time_series?symbol=AAPL&apikey=REDACTED",
        ContentHash: ContentHash.Sha256(payload),
        Payload: payload);

    [Fact]
    public async Task Builds_the_documented_key_layout()
    {
        var store = new LocalRawStore(_root);

        var key = await store.WriteAsync(Envelope(), new DateOnly(2026, 9, 8), TestContext.Current.CancellationToken);

        key.ShouldBe("raw/source=twelvedata/dataset=prices_daily/dt=2026-09-08/AAPL.json");
    }

    [Fact]
    public async Task Round_trips_an_envelope_including_the_exact_payload()
    {
        var store = new LocalRawStore(_root);
        var original = Envelope("{\"values\":[{\"close\":\"328.31000\"}]}");

        var key = await store.WriteAsync(original, new DateOnly(2026, 9, 8), TestContext.Current.CancellationToken);
        var read = await store.ReadAsync(key, TestContext.Current.CancellationToken);

        read.ShouldBe(original);
        read.Payload.ShouldBe(original.Payload);
        read.ObservedAt.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task Lists_keys_under_a_prefix()
    {
        var store = new LocalRawStore(_root);
        await store.WriteAsync(Envelope(), new DateOnly(2026, 9, 8), TestContext.Current.CancellationToken);
        await store.WriteAsync(Envelope() with { Symbol = "MSFT" }, new DateOnly(2026, 9, 8), TestContext.Current.CancellationToken);

        var keys = new List<string>();
        await foreach (var k in store.ListAsync("raw/source=twelvedata", TestContext.Current.CancellationToken))
        {
            keys.Add(k);
        }

        keys.Count.ShouldBe(2);
        keys.ShouldAllBe(k => k.StartsWith("raw/source=twelvedata"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/MarketData.Storage.Tests --filter LocalRawStoreTests`
Expected: FAIL — `LocalRawStore` does not exist.

- [ ] **Step 3: Write `RawKey.cs` and `IRawStore.cs`**

```csharp
namespace MarketData.Storage;

/// <summary>Builds the documented <c>raw/</c> key layout. Keys always use forward slashes.</summary>
public static class RawKey
{
    public static string For(string sourceId, string dataset, DateOnly runDate, string symbol) =>
        $"raw/source={sourceId}/dataset={dataset}/dt={runDate:yyyy-MM-dd}/{symbol}.json";
}
```

```csharp
namespace MarketData.Storage;

/// <summary>
/// The permanent store of record. Objects are written once and never modified.
/// </summary>
public interface IRawStore
{
    /// <summary>Writes an envelope and returns the key it was written to.</summary>
    Task<string> WriteAsync(RawEnvelope envelope, DateOnly runDate, CancellationToken ct);

    Task<RawEnvelope> ReadAsync(string key, CancellationToken ct);

    IAsyncEnumerable<string> ListAsync(string keyPrefix, CancellationToken ct);
}
```

- [ ] **Step 4: Write `Local/LocalRawStore.cs`**

```csharp
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MarketData.Storage.Local;

/// <summary>Filesystem-backed <see cref="IRawStore"/>, used for local runs and tests.</summary>
public sealed class LocalRawStore(string rootDirectory) : IRawStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public async Task<string> WriteAsync(RawEnvelope envelope, DateOnly runDate, CancellationToken ct)
    {
        var key = RawKey.For(envelope.SourceId, envelope.Dataset, runDate, envelope.Symbol);
        var path = ToPath(key);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(envelope, Json), ct);

        return key;
    }

    public async Task<RawEnvelope> ReadAsync(string key, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(ToPath(key), ct);

        return JsonSerializer.Deserialize<RawEnvelope>(text, Json)
               ?? throw new InvalidDataException($"Raw object '{key}' deserialized to null.");
    }

    public async IAsyncEnumerable<string> ListAsync(
        string keyPrefix,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var root = Path.Combine(rootDirectory, "raw");
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var key = Path.GetRelativePath(rootDirectory, path).Replace('\\', '/');
            if (key.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                yield return key;
            }
        }

        await Task.CompletedTask;
    }

    private string ToPath(string key) =>
        Path.Combine(rootDirectory, key.Replace('/', Path.DirectorySeparatorChar));
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/MarketData.Storage.Tests --filter LocalRawStoreTests`
Expected: PASS — 3 tests.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add filesystem raw store behind IRawStore

Key layout is asserted directly so the S3 implementation cannot drift
from it."
```

---

### Task 11: Curated Parquet writer

**Files:**
- Create: `src/MarketData.Storage/Parquet/PriceRow.cs`, `src/MarketData.Storage/Parquet/CorporateActionRow.cs`, `src/MarketData.Storage/ICuratedStore.cs`, `src/MarketData.Storage/Local/LocalCuratedStore.cs`
- Test: `tests/MarketData.Storage.Tests/LocalCuratedStoreTests.cs`

**Interfaces:**
- Consumes: `DailyBar`, `CorporateAction` from Task 7.
- Produces: `ICuratedStore` with `AppendPricesAsync(IReadOnlyList<DailyBar>, string ingestId, CancellationToken) -> Task<IReadOnlyList<string>>` and `AppendActionsAsync(IReadOnlyList<CorporateAction>, string ingestId, CancellationToken) -> Task<IReadOnlyList<string>>`, both returning written part keys. Task 12 reads these files back through DuckDB.

Rows spanning multiple years produce one part per year, because curated data is partitioned by `year` of `effective_date`.

- [ ] **Step 1: Add Parquet.Net to the storage project**

```bash
dotnet add src/MarketData.Storage package Parquet.Net
```
Strip the `Version=` attribute.

- [ ] **Step 2: Write the failing test**

```csharp
using MarketData.Domain;
using MarketData.Storage.Local;
using Shouldly;

namespace MarketData.Storage.Tests;

public sealed class LocalCuratedStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pitmd-" + Guid.NewGuid().ToString("N"));

    private static DailyBar Bar(int year, decimal close) => new(
        "AAPL", new DateOnly(year, 8, 31),
        127.58m, 131.00m, 126.00m, close, 225_702_700L,
        "USD", "twelvedata",
        new DateTimeOffset(year, 8, 31, 20, 15, 0, TimeSpan.Zero),
        ObservationKind.Inferred, "run-1", "raw/x.json");

    [Fact]
    public async Task Writes_one_part_per_year()
    {
        var store = new LocalCuratedStore(_root);

        var keys = await store.AppendPricesAsync(
            [Bar(2020, 129.04m), Bar(2021, 152.51m)], "run-1", TestContext.Current.CancellationToken);

        keys.Count.ShouldBe(2);
        keys.ShouldContain(k => k.Contains("year=2020"));
        keys.ShouldContain(k => k.Contains("year=2021"));
        keys.ShouldAllBe(k => k.StartsWith("curated/dataset=prices_daily/"));
    }

    [Fact]
    public async Task Creates_a_readable_parquet_file_on_disk()
    {
        var store = new LocalCuratedStore(_root);

        var keys = await store.AppendPricesAsync([Bar(2020, 129.04m)], "run-1", TestContext.Current.CancellationToken);

        var path = Path.Combine(_root, keys[0].Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue();
        new FileInfo(path).Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Two_appends_produce_two_distinct_parts()
    {
        var store = new LocalCuratedStore(_root);

        var first = await store.AppendPricesAsync([Bar(2020, 129.04m)], "run-1", TestContext.Current.CancellationToken);
        var second = await store.AppendPricesAsync([Bar(2020, 129.99m)], "run-2", TestContext.Current.CancellationToken);

        second[0].ShouldNotBe(first[0]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
```

The third test guards the append-only rule: a second write must never overwrite the first part.

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/MarketData.Storage.Tests --filter LocalCuratedStoreTests`
Expected: FAIL — `LocalCuratedStore` does not exist.

- [ ] **Step 4: Write the Parquet row DTOs**

These are persistence types, deliberately primitive. Keeping them separate from the domain records means Parquet.Net's type mapping is never load-bearing on the domain model.

```csharp
namespace MarketData.Storage.Parquet;

/// <summary>Persistence shape of one <c>prices_daily</c> row. Public for Parquet.Net reflection.</summary>
public sealed class PriceRow
{
    public string Symbol { get; set; } = string.Empty;
    public DateOnly EffectiveDate { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Volume { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime ObservedAt { get; set; }
    public string ObservedAtKind { get; set; } = string.Empty;
    public string IngestId { get; set; } = string.Empty;
    public string RawKey { get; set; } = string.Empty;
}
```

```csharp
namespace MarketData.Storage.Parquet;

/// <summary>Persistence shape of one <c>corporate_actions</c> row.</summary>
public sealed class CorporateActionRow
{
    public string Symbol { get; set; } = string.Empty;
    public DateOnly ExDate { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public decimal? Ratio { get; set; }
    public decimal? Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime ObservedAt { get; set; }
    public string ObservedAtKind { get; set; } = string.Empty;
    public string IngestId { get; set; } = string.Empty;
    public string RawKey { get; set; } = string.Empty;
}
```

- [ ] **Step 5: Write `ICuratedStore.cs` and `Local/LocalCuratedStore.cs`**

```csharp
using MarketData.Domain;

namespace MarketData.Storage;

/// <summary>
/// Derived, append-only Parquet store. Every append writes new part files;
/// nothing is ever updated or deleted.
/// </summary>
public interface ICuratedStore
{
    Task<IReadOnlyList<string>> AppendPricesAsync(
        IReadOnlyList<DailyBar> bars, string ingestId, CancellationToken ct);

    Task<IReadOnlyList<string>> AppendActionsAsync(
        IReadOnlyList<CorporateAction> actions, string ingestId, CancellationToken ct);
}
```

```csharp
using MarketData.Domain;
using MarketData.Storage.Parquet;
using Parquet.Serialization;

namespace MarketData.Storage.Local;

/// <summary>Filesystem-backed <see cref="ICuratedStore"/>.</summary>
public sealed class LocalCuratedStore(string rootDirectory) : ICuratedStore
{
    public Task<IReadOnlyList<string>> AppendPricesAsync(
        IReadOnlyList<DailyBar> bars, string ingestId, CancellationToken ct) =>
        WritePartsAsync("prices_daily", bars, b => b.EffectiveDate.Year, ToRow, ingestId, ct);

    public Task<IReadOnlyList<string>> AppendActionsAsync(
        IReadOnlyList<CorporateAction> actions, string ingestId, CancellationToken ct) =>
        WritePartsAsync("corporate_actions", actions, a => a.ExDate.Year, ToRow, ingestId, ct);

    private async Task<IReadOnlyList<string>> WritePartsAsync<TFact, TRow>(
        string dataset,
        IReadOnlyList<TFact> facts,
        Func<TFact, int> yearOf,
        Func<TFact, TRow> toRow,
        string ingestId,
        CancellationToken ct)
        where TRow : new()
    {
        var keys = new List<string>();

        foreach (var group in facts.GroupBy(yearOf).OrderBy(g => g.Key))
        {
            var key = $"curated/dataset={dataset}/year={group.Key}/part-{ingestId}-{Guid.NewGuid():N}.parquet";
            var path = Path.Combine(rootDirectory, key.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var rows = group.Select(toRow).ToList();
            await using var stream = File.Create(path);
            await ParquetSerializer.SerializeAsync(rows, stream, cancellationToken: ct);

            keys.Add(key);
        }

        return keys;
    }

    private static PriceRow ToRow(DailyBar b) => new()
    {
        Symbol = b.Symbol,
        EffectiveDate = b.EffectiveDate,
        Open = b.Open, High = b.High, Low = b.Low, Close = b.Close,
        Volume = b.Volume,
        Currency = b.Currency,
        Source = b.Source,
        ObservedAt = b.ObservedAt.UtcDateTime,
        ObservedAtKind = b.ObservedAtKind.ToString().ToUpperInvariant(),
        IngestId = b.IngestId,
        RawKey = b.RawKey
    };

    private static CorporateActionRow ToRow(CorporateAction a) => new()
    {
        Symbol = a.Symbol,
        ExDate = a.ExDate,
        ActionType = a.ActionType.ToString().ToUpperInvariant(),
        Ratio = a.Ratio,
        Amount = a.Amount,
        Currency = a.Currency,
        Source = a.Source,
        ObservedAt = a.ObservedAt.UtcDateTime,
        ObservedAtKind = a.ObservedAtKind.ToString().ToUpperInvariant(),
        IngestId = a.IngestId,
        RawKey = a.RawKey
    };
}
```

The `Guid` in the part name is what guarantees two appends never collide, satisfying the append-only rule even when two runs share an `ingestId`.

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/MarketData.Storage.Tests --filter LocalCuratedStoreTests`
Expected: PASS — 3 tests.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Add curated Parquet writer partitioned by year

Persistence DTOs are separate from domain records so Parquet.Net type
mapping never becomes load-bearing on the domain model."
```

---

### Task 12: Read curated Parquet back through DuckDB

**Files:**
- Create: `tests/MarketData.Storage.Tests/DuckDbReader.cs`
- Test: `tests/MarketData.Storage.Tests/CuratedRoundTripTests.cs`

**Interfaces:**
- Consumes: `LocalCuratedStore` from Task 11.
- Produces: `DuckDbReader.Query(string sql) -> IReadOnlyList<IReadOnlyDictionary<string, object?>>`, reused by every later storage test.

Parquet.Net 6's class deserializer fails on plain `string` properties, and DuckDB is the production read path regardless — so tests verify writes by reading through DuckDB, not by round-tripping the writer.

- [ ] **Step 1: Add DuckDB to the test project**

```bash
dotnet add tests/MarketData.Storage.Tests package DuckDB.NET.Data.Full
```
Strip the `Version=` attribute.

- [ ] **Step 2: Write `DuckDbReader.cs`**

```csharp
using System.Data;
using DuckDB.NET.Data;

namespace MarketData.Storage.Tests;

/// <summary>In-memory DuckDB used to read Parquet exactly as the query layer will.</summary>
public sealed class DuckDbReader : IDisposable
{
    private readonly DuckDBConnection _connection;

    public DuckDbReader()
    {
        _connection = new DuckDBConnection("DataSource=:memory:");
        _connection.Open();
    }

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;

        using var reader = command.ExecuteReader();
        var rows = new List<IReadOnlyDictionary<string, object?>>();

        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Glob matching every Parquet part of a dataset under a local root.</summary>
    public static string Glob(string root, string dataset) =>
        Path.Combine(root, "curated", $"dataset={dataset}", "**", "*.parquet").Replace('\\', '/');

    public void Dispose() => _connection.Dispose();
}
```

- [ ] **Step 3: Write the failing round-trip test**

```csharp
using MarketData.Domain;
using MarketData.Storage.Local;
using Shouldly;

namespace MarketData.Storage.Tests;

public sealed class CuratedRoundTripTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pitmd-" + Guid.NewGuid().ToString("N"));

    private static DailyBar Bar(decimal close, DateTimeOffset observedAt, ObservationKind kind) => new(
        "AAPL", new DateOnly(2020, 8, 31),
        127.58m, 131.00m, 126.00m, close, 225_702_700L,
        "USD", "twelvedata", observedAt, kind, "run-1", "raw/x.json");

    [Fact]
    public async Task Values_survive_the_write_exactly()
    {
        var store = new LocalCuratedStore(_root);
        var observedAt = new DateTimeOffset(2020, 8, 31, 20, 15, 0, TimeSpan.Zero);
        await store.AppendPricesAsync(
            [Bar(328.31000m, observedAt, ObservationKind.Inferred)], "run-1", TestContext.Current.CancellationToken);

        using var duck = new DuckDbReader();
        var rows = duck.Query(
            $"SELECT Symbol, CAST(EffectiveDate AS DATE) AS d, Close, Volume, ObservedAtKind " +
            $"FROM read_parquet('{DuckDbReader.Glob(_root, "prices_daily")}')");

        rows.Count.ShouldBe(1);
        rows[0]["Symbol"]!.ToString().ShouldBe("AAPL");
        rows[0]["d"].ShouldBe(new DateTime(2020, 8, 31));
        Convert.ToDecimal(rows[0]["Close"]).ShouldBe(328.31000m);
        Convert.ToInt64(rows[0]["Volume"]).ShouldBe(225_702_700L);
        rows[0]["ObservedAtKind"]!.ToString().ShouldBe("INFERRED");
    }

    [Fact]
    public async Task Observed_at_is_stored_as_utc()
    {
        var store = new LocalCuratedStore(_root);
        var observedAt = new DateTimeOffset(2020, 8, 31, 20, 15, 0, TimeSpan.Zero);
        await store.AppendPricesAsync(
            [Bar(129.04m, observedAt, ObservationKind.Observed)], "run-1", TestContext.Current.CancellationToken);

        using var duck = new DuckDbReader();
        var rows = duck.Query(
            $"SELECT ObservedAt FROM read_parquet('{DuckDbReader.Glob(_root, "prices_daily")}')");

        Convert.ToDateTime(rows[0]["ObservedAt"]).ShouldBe(new DateTime(2020, 8, 31, 20, 15, 0));
    }

    [Fact]
    public async Task Multiple_parts_are_read_as_one_dataset()
    {
        var store = new LocalCuratedStore(_root);
        var at = new DateTimeOffset(2020, 8, 31, 20, 15, 0, TimeSpan.Zero);
        await store.AppendPricesAsync([Bar(129.04m, at, ObservationKind.Inferred)], "run-1", TestContext.Current.CancellationToken);
        await store.AppendPricesAsync([Bar(129.99m, at.AddDays(1), ObservationKind.Observed)], "run-2", TestContext.Current.CancellationToken);

        using var duck = new DuckDbReader();
        var rows = duck.Query(
            $"SELECT COUNT(*) AS n FROM read_parquet('{DuckDbReader.Glob(_root, "prices_daily")}')");

        Convert.ToInt64(rows[0]["n"]).ShouldBe(2L);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
```

The third test is the one that matters most: it proves an append-only store of many small parts reads as a single dataset, which is the assumption the whole query layer rests on.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/MarketData.Storage.Tests --filter CuratedRoundTripTests`
Expected: PASS — 3 tests.

If `read_parquet` reports no files, print the glob and confirm the `**` recursion matches `year=YYYY`. If `EffectiveDate` comes back as a timestamp rather than a date, the explicit `CAST(... AS DATE)` already handles it — do not change the writer.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Verify curated Parquet through DuckDB rather than the writer

Parquet.Net 6's class deserializer fails on string properties, and
DuckDB is the production read path, so tests assert what production
will actually see."
```

---

### Task 13: S3-backed stores, verified against LocalStack

**Files:**
- Create: `src/MarketData.Storage/S3/S3RawStore.cs`, `src/MarketData.Storage/S3/S3CuratedStore.cs`
- Create: `tests/MarketData.Integration.Tests/MarketData.Integration.Tests.csproj`, `tests/MarketData.Integration.Tests/LocalStackFixture.cs`, `tests/MarketData.Integration.Tests/S3RawStoreTests.cs`

**Interfaces:**
- Consumes: `IRawStore`, `ICuratedStore`, `RawKey` from Tasks 10 and 11.
- Produces: `S3RawStore(IAmazonS3, string bucket)` and `S3CuratedStore(IAmazonS3, string bucket)`, selected by configuration in Task 19.

These tests require Docker. They are excluded from the default CI job by an xUnit trait.

- [ ] **Step 1: Create the integration test project**

```bash
dotnet new xunit3 -o tests/MarketData.Integration.Tests -f net10.0
rm -f tests/MarketData.Integration.Tests/UnitTest1.cs
dotnet sln add tests/MarketData.Integration.Tests
dotnet add tests/MarketData.Integration.Tests reference src/MarketData.Storage
dotnet add tests/MarketData.Integration.Tests package Testcontainers.LocalStack
dotnet add tests/MarketData.Integration.Tests package AWSSDK.S3
dotnet add tests/MarketData.Integration.Tests package Shouldly
dotnet add src/MarketData.Storage package AWSSDK.S3
```
Strip every `Version=` attribute.

- [ ] **Step 2: Write `LocalStackFixture.cs`**

```csharp
using Amazon.S3;
using DotNet.Testcontainers.Builders;
using Testcontainers.LocalStack;

namespace MarketData.Integration.Tests;

/// <summary>Starts LocalStack once per test collection and exposes a configured S3 client.</summary>
public sealed class LocalStackFixture : IAsyncLifetime
{
    private readonly LocalStackContainer _container = new LocalStackBuilder()
        .WithImage("localstack/localstack:3")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(4566))
        .Build();

    public IAmazonS3 S3 { get; private set; } = null!;

    public string Bucket => "pit-marketdata-test";

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        S3 = new AmazonS3Client(
            "test",
            "test",
            new AmazonS3Config
            {
                ServiceURL = _container.GetConnectionString(),
                ForcePathStyle = true,
                AuthenticationRegion = "ap-southeast-2"
            });

        await S3.PutBucketAsync(Bucket);
    }

    public async ValueTask DisposeAsync()
    {
        S3.Dispose();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(nameof(LocalStackCollection))]
public sealed class LocalStackCollection : ICollectionFixture<LocalStackFixture>;
```

- [ ] **Step 3: Write the failing test**

```csharp
using MarketData.Storage;
using MarketData.Storage.S3;
using Shouldly;

namespace MarketData.Integration.Tests;

[Collection(nameof(LocalStackCollection))]
[Trait("Category", "Integration")]
public sealed class S3RawStoreTests(LocalStackFixture fixture)
{
    private static RawEnvelope Envelope(string symbol = "AAPL")
    {
        const string payload = "{\"values\":[{\"close\":\"328.31000\"}]}";

        return new RawEnvelope(
            "twelvedata", symbol, "prices_daily",
            new DateTimeOffset(2026, 9, 8, 22, 15, 3, TimeSpan.Zero),
            "https://api.twelvedata.com/time_series?symbol=AAPL&apikey=REDACTED",
            ContentHash.Sha256(payload), payload);
    }

    [Fact]
    public async Task Round_trips_through_s3_with_the_same_key_layout_as_local()
    {
        var store = new S3RawStore(fixture.S3, fixture.Bucket);
        var original = Envelope();

        var key = await store.WriteAsync(original, new DateOnly(2026, 9, 8), TestContext.Current.CancellationToken);
        var read = await store.ReadAsync(key, TestContext.Current.CancellationToken);

        key.ShouldBe("raw/source=twelvedata/dataset=prices_daily/dt=2026-09-08/AAPL.json");
        read.ShouldBe(original);
    }

    [Fact]
    public async Task Lists_keys_under_a_prefix()
    {
        var store = new S3RawStore(fixture.S3, fixture.Bucket);
        await store.WriteAsync(Envelope("MSFT"), new DateOnly(2026, 9, 9), TestContext.Current.CancellationToken);

        var keys = new List<string>();
        await foreach (var k in store.ListAsync("raw/source=twelvedata", TestContext.Current.CancellationToken))
        {
            keys.Add(k);
        }

        keys.ShouldContain(k => k.EndsWith("MSFT.json"));
    }
}
```

- [ ] **Step 4: Run to verify it fails**

Run: `dotnet test tests/MarketData.Integration.Tests`
Expected: FAIL — `S3RawStore` does not exist. If it fails to start LocalStack instead, Docker is not running; start Docker Desktop.

- [ ] **Step 5: Write `S3/S3RawStore.cs`**

```csharp
using System.Runtime.CompilerServices;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;

namespace MarketData.Storage.S3;

/// <summary>S3-backed <see cref="IRawStore"/>. Objects are written once and never modified.</summary>
public sealed class S3RawStore(IAmazonS3 s3, string bucket) : IRawStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public async Task<string> WriteAsync(RawEnvelope envelope, DateOnly runDate, CancellationToken ct)
    {
        var key = RawKey.For(envelope.SourceId, envelope.Dataset, runDate, envelope.Symbol);

        await s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ContentBody = JsonSerializer.Serialize(envelope, Json),
                ContentType = "application/json"
            },
            ct);

        return key;
    }

    public async Task<RawEnvelope> ReadAsync(string key, CancellationToken ct)
    {
        using var response = await s3.GetObjectAsync(bucket, key, ct);
        using var reader = new StreamReader(response.ResponseStream);

        var text = await reader.ReadToEndAsync(ct);

        return JsonSerializer.Deserialize<RawEnvelope>(text, Json)
               ?? throw new InvalidDataException($"Raw object '{key}' deserialized to null.");
    }

    public async IAsyncEnumerable<string> ListAsync(
        string keyPrefix,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string? token = null;

        do
        {
            var page = await s3.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = bucket, Prefix = keyPrefix, ContinuationToken = token },
                ct);

            foreach (var o in page.S3Objects)
            {
                yield return o.Key;
            }

            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (token is not null);
    }
}
```

- [ ] **Step 6: Write `S3/S3CuratedStore.cs`**

```csharp
using Amazon.S3;
using Amazon.S3.Model;
using MarketData.Domain;
using MarketData.Storage.Local;

namespace MarketData.Storage.S3;

/// <summary>
/// S3-backed <see cref="ICuratedStore"/>. Parts are built locally in a temporary
/// directory by <see cref="LocalCuratedStore"/>, then uploaded under the same keys,
/// so both implementations produce byte-equivalent layouts.
/// </summary>
public sealed class S3CuratedStore(IAmazonS3 s3, string bucket) : ICuratedStore
{
    public Task<IReadOnlyList<string>> AppendPricesAsync(
        IReadOnlyList<DailyBar> bars, string ingestId, CancellationToken ct) =>
        StageAndUploadAsync((local, c) => local.AppendPricesAsync(bars, ingestId, c), ct);

    public Task<IReadOnlyList<string>> AppendActionsAsync(
        IReadOnlyList<CorporateAction> actions, string ingestId, CancellationToken ct) =>
        StageAndUploadAsync((local, c) => local.AppendActionsAsync(actions, ingestId, c), ct);

    private async Task<IReadOnlyList<string>> StageAndUploadAsync(
        Func<LocalCuratedStore, CancellationToken, Task<IReadOnlyList<string>>> write,
        CancellationToken ct)
    {
        var staging = Path.Combine(Path.GetTempPath(), "pitmd-stage-" + Guid.NewGuid().ToString("N"));

        try
        {
            var keys = await write(new LocalCuratedStore(staging), ct);

            foreach (var key in keys)
            {
                var path = Path.Combine(staging, key.Replace('/', Path.DirectorySeparatorChar));

                await s3.PutObjectAsync(
                    new PutObjectRequest { BucketName = bucket, Key = key, FilePath = path },
                    ct);
            }

            return keys;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }
}
```

- [ ] **Step 7: Exclude integration tests from the default CI job**

In `.github/workflows/ci.yml`, change the test step to:

```yaml
      - name: Test
        run: dotnet test --no-build --configuration Release --verbosity normal --filter "Category!=Integration"
```

- [ ] **Step 8: Run everything**

Run: `dotnet test` (with Docker running)
Expected: PASS — all unit and integration tests.
Run: `dotnet test --filter "Category!=Integration"`
Expected: PASS, skipping the LocalStack tests. This is what CI runs.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "Add S3 stores verified against LocalStack

The S3 curated store stages parts through the local writer so both
implementations produce identical key layouts by construction."
```

---

### Task 14: Instruments, bitemporal watchlist, and fetch cursors in DynamoDB

**Files:**
- Create: `src/MarketData.Domain/Instrument.cs`
- Create: `src/MarketData.Storage/Dynamo/WatchlistRepository.cs`, `src/MarketData.Storage/Dynamo/CursorRepository.cs`, `src/MarketData.Storage/Dynamo/Cursor.cs`, `src/MarketData.Storage/Dynamo/InstrumentRepository.cs`
- Test: `tests/MarketData.Integration.Tests/WatchlistRepositoryTests.cs`, `tests/MarketData.Integration.Tests/InstrumentRepositoryTests.cs`

**Interfaces:**
- Consumes: the DynamoDB table from Task 4.
- Produces: `IWatchlistRepository` with `AddAsync(string symbol, DateTimeOffset addedAt, CancellationToken)`, `RemoveAsync(string symbol, DateTimeOffset removedAt, CancellationToken)` and `ActiveAsync(DateTimeOffset asOf, CancellationToken) -> Task<IReadOnlyList<string>>`; `ICursorRepository` with `GetAsync(string dataset, string symbol, CancellationToken) -> Task<Cursor?>` and `SaveAsync(Cursor, CancellationToken)`; `IInstrumentRepository` with `GetAsync(string symbol, CancellationToken) -> Task<Instrument?>` and `UpsertAsync(Instrument, CancellationToken)`. Task 19 drives the cursors; the CLI in the next plan drives instruments and the watchlist.

`ActiveAsync` taking an `asOf` is the point of this task: without it the universe itself is not point-in-time, and every historical query silently knows about symbols that were never being tracked.

- [ ] **Step 1: Add the LocalStack DynamoDB service and packages**

In `LocalStackFixture`, add a DynamoDB client alongside S3:

```csharp
    public IAmazonDynamoDB Dynamo { get; private set; } = null!;

    public string TableName => "pit-marketdata-test";
```

and in `InitializeAsync`, after the bucket is created:

```csharp
        Dynamo = new AmazonDynamoDBClient(
            "test",
            "test",
            new AmazonDynamoDBConfig
            {
                ServiceURL = _container.GetConnectionString(),
                AuthenticationRegion = "ap-southeast-2"
            });

        await Dynamo.CreateTableAsync(new CreateTableRequest
        {
            TableName = TableName,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            KeySchema =
            [
                new KeySchemaElement("pk", KeyType.HASH),
                new KeySchemaElement("sk", KeyType.RANGE)
            ],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S)
            ]
        });
```

Dispose it in `DisposeAsync`. Then:

```bash
dotnet add src/MarketData.Storage package AWSSDK.DynamoDBv2
dotnet add tests/MarketData.Integration.Tests package AWSSDK.DynamoDBv2
```
Strip the `Version=` attributes.

- [ ] **Step 2: Write the failing test**

```csharp
using MarketData.Storage.Dynamo;
using Shouldly;

namespace MarketData.Integration.Tests;

[Collection(nameof(LocalStackCollection))]
[Trait("Category", "Integration")]
public sealed class WatchlistRepositoryTests(LocalStackFixture fixture)
{
    private static readonly DateTimeOffset Added = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Removed = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private WatchlistRepository Repo() => new(fixture.Dynamo, fixture.TableName);

    [Fact]
    public async Task Symbol_is_invisible_before_it_was_added()
    {
        var repo = Repo();
        await repo.AddAsync("NVDA", Added, TestContext.Current.CancellationToken);

        var active = await repo.ActiveAsync(
            new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);

        active.ShouldNotContain("NVDA");
    }

    [Fact]
    public async Task Symbol_is_visible_between_added_and_removed()
    {
        var repo = Repo();
        await repo.AddAsync("TSLA", Added, TestContext.Current.CancellationToken);
        await repo.RemoveAsync("TSLA", Removed, TestContext.Current.CancellationToken);

        var active = await repo.ActiveAsync(
            new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);

        active.ShouldContain("TSLA");
    }

    [Fact]
    public async Task Symbol_is_invisible_after_it_was_removed()
    {
        var repo = Repo();
        await repo.AddAsync("META", Added, TestContext.Current.CancellationToken);
        await repo.RemoveAsync("META", Removed, TestContext.Current.CancellationToken);

        var active = await repo.ActiveAsync(
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);

        active.ShouldNotContain("META");
    }

    [Fact]
    public async Task Removal_is_a_soft_delete_that_preserves_the_record()
    {
        var repo = Repo();
        await repo.AddAsync("AMD", Added, TestContext.Current.CancellationToken);
        await repo.RemoveAsync("AMD", Removed, TestContext.Current.CancellationToken);

        // Still visible as at a date before removal: the row was not deleted.
        var active = await repo.ActiveAsync(Added.AddDays(1), TestContext.Current.CancellationToken);

        active.ShouldContain("AMD");
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/MarketData.Integration.Tests --filter WatchlistRepositoryTests`
Expected: FAIL — `WatchlistRepository` does not exist.

- [ ] **Step 4: Write `Dynamo/WatchlistRepository.cs`**

```csharp
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace MarketData.Storage.Dynamo;

/// <summary>Universe membership over time. Removal is a soft delete.</summary>
public interface IWatchlistRepository
{
    Task AddAsync(string symbol, DateTimeOffset addedAt, CancellationToken ct);

    Task RemoveAsync(string symbol, DateTimeOffset removedAt, CancellationToken ct);

    /// <summary>Symbols being tracked as at <paramref name="asOf"/>.</summary>
    Task<IReadOnlyList<string>> ActiveAsync(DateTimeOffset asOf, CancellationToken ct);
}

/// <inheritdoc />
public sealed class WatchlistRepository(IAmazonDynamoDB dynamo, string tableName) : IWatchlistRepository
{
    private const string PartitionKey = "WATCHLIST";

    public Task AddAsync(string symbol, DateTimeOffset addedAt, CancellationToken ct) =>
        dynamo.PutItemAsync(
            new PutItemRequest
            {
                TableName = tableName,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new(PartitionKey),
                    ["sk"] = new($"SYMBOL#{symbol}"),
                    ["symbol"] = new(symbol),
                    ["added_at"] = new(Iso(addedAt))
                }
            },
            ct);

    public Task RemoveAsync(string symbol, DateTimeOffset removedAt, CancellationToken ct) =>
        dynamo.UpdateItemAsync(
            new UpdateItemRequest
            {
                TableName = tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new(PartitionKey),
                    ["sk"] = new($"SYMBOL#{symbol}")
                },
                UpdateExpression = "SET removed_at = :r",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":r"] = new(Iso(removedAt))
                }
            },
            ct);

    public async Task<IReadOnlyList<string>> ActiveAsync(DateTimeOffset asOf, CancellationToken ct)
    {
        var response = await dynamo.QueryAsync(
            new QueryRequest
            {
                TableName = tableName,
                KeyConditionExpression = "pk = :pk",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new(PartitionKey)
                }
            },
            ct);

        var stamp = Iso(asOf);

        return response.Items
            .Where(item => string.CompareOrdinal(item["added_at"].S, stamp) <= 0)
            .Where(item => !item.TryGetValue("removed_at", out var removed)
                           || string.CompareOrdinal(removed.S, stamp) > 0)
            .Select(item => item["symbol"].S)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    // Round-trip ISO-8601 in UTC sorts lexicographically in the same order as
    // chronologically, which is what makes the string comparisons above correct.
    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
}
```

- [ ] **Step 5: Write `Dynamo/Cursor.cs` and `Dynamo/CursorRepository.cs`**

```csharp
namespace MarketData.Storage.Dynamo;

/// <summary>Watermark for one (dataset, symbol) fetch stream.</summary>
public sealed record Cursor(
    string Dataset,
    string Symbol,
    DateOnly? LastEffectiveDate,
    string? LastContentHash);
```

```csharp
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace MarketData.Storage.Dynamo;

public interface ICursorRepository
{
    Task<Cursor?> GetAsync(string dataset, string symbol, CancellationToken ct);

    Task SaveAsync(Cursor cursor, CancellationToken ct);
}

/// <inheritdoc />
public sealed class CursorRepository(IAmazonDynamoDB dynamo, string tableName) : ICursorRepository
{
    public async Task<Cursor?> GetAsync(string dataset, string symbol, CancellationToken ct)
    {
        var response = await dynamo.GetItemAsync(
            new GetItemRequest { TableName = tableName, Key = Key(dataset, symbol) }, ct);

        if (response.Item is null || response.Item.Count == 0)
        {
            return null;
        }

        var last = response.Item.TryGetValue("last_effective_date", out var d) && d.S is { Length: > 0 }
            ? DateOnly.ParseExact(d.S, "yyyy-MM-dd")
            : (DateOnly?)null;

        var hash = response.Item.TryGetValue("last_content_hash", out var h) ? h.S : null;

        return new Cursor(dataset, symbol, last, hash);
    }

    public Task SaveAsync(Cursor cursor, CancellationToken ct)
    {
        var item = Key(cursor.Dataset, cursor.Symbol);
        item["last_content_hash"] = new AttributeValue(cursor.LastContentHash ?? string.Empty);
        item["last_effective_date"] = new AttributeValue(
            cursor.LastEffectiveDate?.ToString("yyyy-MM-dd") ?? string.Empty);

        return dynamo.PutItemAsync(new PutItemRequest { TableName = tableName, Item = item }, ct);
    }

    private static Dictionary<string, AttributeValue> Key(string dataset, string symbol) => new()
    {
        ["pk"] = new($"CURSOR#{dataset}"),
        ["sk"] = new($"SYMBOL#{symbol}")
    };
}
```

- [ ] **Step 6: Write the failing instrument test**

Instrument reference data is what `cik` hangs off, and `cik` is the join key the EDGAR stage depends on. Getting it stored now costs nothing; retrofitting it after symbols exist means backfilling by hand.

```csharp
using MarketData.Domain;
using MarketData.Storage.Dynamo;
using Shouldly;

namespace MarketData.Integration.Tests;

[Collection(nameof(LocalStackCollection))]
[Trait("Category", "Integration")]
public sealed class InstrumentRepositoryTests(LocalStackFixture fixture)
{
    private InstrumentRepository Repo() => new(fixture.Dynamo, fixture.TableName);

    private static Instrument Apple() => new(
        Symbol: "AAPL",
        Exchange: "NASDAQ",
        MicCode: "XNGS",
        Name: "Apple Inc.",
        Currency: "USD",
        Cik: "320193",
        Sector: "Technology",
        ListingDate: new DateOnly(1980, 12, 12),
        FirstSeenAt: new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero),
        IsActive: true);

    [Fact]
    public async Task Round_trips_every_field_including_cik()
    {
        var repo = Repo();
        await repo.UpsertAsync(Apple(), TestContext.Current.CancellationToken);

        var read = await repo.GetAsync("AAPL", TestContext.Current.CancellationToken);

        read.ShouldBe(Apple());
        read!.Cik.ShouldBe("320193");
    }

    [Fact]
    public async Task Returns_null_for_an_unknown_symbol()
    {
        (await Repo().GetAsync("NOPE", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Tolerates_a_missing_cik_and_sector()
    {
        var repo = Repo();
        var etf = Apple() with { Symbol = "SPY", Cik = null, Sector = null, ListingDate = null };
        await repo.UpsertAsync(etf, TestContext.Current.CancellationToken);

        var read = await repo.GetAsync("SPY", TestContext.Current.CancellationToken);

        read!.Cik.ShouldBeNull();
        read.Sector.ShouldBeNull();
        read.ListingDate.ShouldBeNull();
    }
}
```

- [ ] **Step 7: Write `src/MarketData.Domain/Instrument.cs`**

```csharp
namespace MarketData.Domain;

/// <summary>
/// Reference data for one tradeable instrument. <see cref="Cik"/> is the SEC
/// identifier used to join fundamentals, and is null for instruments that do not
/// file (ETFs, indices).
/// </summary>
public sealed record Instrument(
    string Symbol,
    string Exchange,
    string MicCode,
    string Name,
    string Currency,
    string? Cik,
    string? Sector,
    DateOnly? ListingDate,
    DateTimeOffset FirstSeenAt,
    bool IsActive);
```

- [ ] **Step 8: Write `Dynamo/InstrumentRepository.cs`**

```csharp
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using MarketData.Domain;

namespace MarketData.Storage.Dynamo;

public interface IInstrumentRepository
{
    Task<Instrument?> GetAsync(string symbol, CancellationToken ct);

    Task UpsertAsync(Instrument instrument, CancellationToken ct);
}

/// <inheritdoc />
public sealed class InstrumentRepository(IAmazonDynamoDB dynamo, string tableName) : IInstrumentRepository
{
    public async Task<Instrument?> GetAsync(string symbol, CancellationToken ct)
    {
        var response = await dynamo.GetItemAsync(
            new GetItemRequest { TableName = tableName, Key = Key(symbol) }, ct);

        if (response.Item is null || response.Item.Count == 0)
        {
            return null;
        }

        var item = response.Item;

        return new Instrument(
            Symbol: item["symbol"].S,
            Exchange: item["exchange"].S,
            MicCode: item["mic_code"].S,
            Name: item["name"].S,
            Currency: item["currency"].S,
            Cik: Optional(item, "cik"),
            Sector: Optional(item, "sector"),
            ListingDate: Optional(item, "listing_date") is { } d ? DateOnly.ParseExact(d, "yyyy-MM-dd") : null,
            FirstSeenAt: DateTimeOffset.Parse(item["first_seen_at"].S).ToUniversalTime(),
            IsActive: item["is_active"].BOOL ?? true);
    }

    public Task UpsertAsync(Instrument instrument, CancellationToken ct)
    {
        var item = Key(instrument.Symbol);
        item["symbol"] = new AttributeValue(instrument.Symbol);
        item["exchange"] = new AttributeValue(instrument.Exchange);
        item["mic_code"] = new AttributeValue(instrument.MicCode);
        item["name"] = new AttributeValue(instrument.Name);
        item["currency"] = new AttributeValue(instrument.Currency);
        item["first_seen_at"] = new AttributeValue(
            instrument.FirstSeenAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
        item["is_active"] = new AttributeValue { BOOL = instrument.IsActive };

        Set(item, "cik", instrument.Cik);
        Set(item, "sector", instrument.Sector);
        Set(item, "listing_date", instrument.ListingDate?.ToString("yyyy-MM-dd"));

        return dynamo.PutItemAsync(new PutItemRequest { TableName = tableName, Item = item }, ct);
    }

    // DynamoDB rejects empty strings in some contexts, so absent values are simply
    // not written rather than written as "".
    private static void Set(Dictionary<string, AttributeValue> item, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            item[name] = new AttributeValue(value);
        }
    }

    private static string? Optional(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var v) && v.S is { Length: > 0 } ? v.S : null;

    private static Dictionary<string, AttributeValue> Key(string symbol) => new()
    {
        ["pk"] = new($"INSTRUMENT#{symbol}"),
        ["sk"] = new("META")
    };
}
```

- [ ] **Step 9: Run the tests**

Run: `dotnet test tests/MarketData.Integration.Tests --filter "WatchlistRepositoryTests|InstrumentRepositoryTests"`
Expected: PASS — 7 tests.

Run: `dotnet test tests/MarketData.Domain.Tests`
Expected: PASS — the architecture tests from Task 1 must still pass. `Instrument` is a plain record and adds no package reference to Domain.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "Add instrument, bitemporal watchlist and cursor repositories

ActiveAsync takes an asOf so the tracked universe is itself
point-in-time; without it every historical query would know about
symbols that were not being tracked at the time.

Instruments carry cik from the outset because it is the join key the
EDGAR stage needs, and backfilling it after symbols exist is manual."
```
---

# Stage 3 — Vendor integration

Fetching and parsing are separate interfaces. The fetcher stamps `observed_at` once into the envelope; parsing is a pure function of that envelope; and a separate policy decides which timestamp each bar carries. Keeping those three apart is what makes the curated store rebuildable.

### Task 15: Verify the real API key and store it in SSM

**Files:**
- Create: `scripts/verify-vendor.sh`
- Modify: `docs/superpowers/specs/2026-09-08-layer1-point-in-time-market-data-design.md` (§3 open risk)

**Interfaces:**
- Consumes: nothing.
- Produces: a confirmed API key in SSM at `/pit-marketdata/twelvedata/apikey`, read by Task 19.

The spec's §3 findings were verified with Twelve Data's `demo` key, which may be more permissive than a real free key. Everything downstream assumes those three endpoints work. Confirm before building on them.

- [ ] **Step 1: Get a free API key**

Register at `https://twelvedata.com/register` and copy the API key from the dashboard.

- [ ] **Step 2: Write `scripts/verify-vendor.sh`**

```bash
#!/usr/bin/env bash
# Verifies the Twelve Data free tier provides everything Layer 1 depends on.
# Usage: TWELVEDATA_API_KEY=... ./scripts/verify-vendor.sh
set -euo pipefail

: "${TWELVEDATA_API_KEY:?set TWELVEDATA_API_KEY}"

base="https://api.twelvedata.com"
symbol="AAPL"
fail=0

check() {
  local name="$1" url="$2" expect="$3"
  local body
  body=$(curl -s -m 30 "$url&apikey=${TWELVEDATA_API_KEY}")

  if echo "$body" | grep -q "$expect"; then
    echo "PASS  $name"
  else
    echo "FAIL  $name"
    echo "      $(echo "$body" | head -c 300)"
    fail=1
  fi
}

check "time_series (unadjusted daily bars)" \
  "$base/time_series?symbol=$symbol&interval=1day&outputsize=5000&start_date=2015-01-01" '"values"'

check "splits" "$base/splits?symbol=$symbol&range=full" '"splits"'

check "dividends" "$base/dividends?symbol=$symbol&range=full" '"dividends"'

exit "$fail"
```

- [ ] **Step 3: Run it**

Run: `TWELVEDATA_API_KEY=<your key> bash scripts/verify-vendor.sh`
Expected: three `PASS` lines.

If `splits` or `dividends` fail with a plan-restriction message, stop and record it in the spec's §3. The documented fallback is manual corporate-action entry with `source = MANUAL`; corporate actions then move out of this stage into the CLI. Prices failing is a blocker — reassess the vendor before continuing.

- [ ] **Step 4: Store the key in SSM as a SecureString**

```bash
aws ssm put-parameter \
  --region ap-southeast-2 \
  --name /pit-marketdata/twelvedata/apikey \
  --type SecureString \
  --value "<your key>" \
  --overwrite
```

Verify without printing the value:

```bash
aws ssm get-parameter --region ap-southeast-2 \
  --name /pit-marketdata/twelvedata/apikey --with-decryption \
  --query 'Parameter.Type'
```
Expected: `"SecureString"`.

- [ ] **Step 5: Record the outcome in the spec and commit**

Replace §3's "Open risk" paragraph with what actually happened, dated. Then:

```bash
git add scripts/verify-vendor.sh docs/superpowers/specs
git commit -m "Verify Twelve Data free tier with a real key

Replaces the demo-key caveat in the spec with a dated result, so the
assumption everything downstream rests on is recorded rather than
remembered."
```

---

### Task 16: Twelve Data price source

**Files:**
- Create: `src/MarketData.Sources/MarketData.Sources.csproj`, `src/MarketData.Sources/IPriceSource.cs`, `src/MarketData.Sources/RateLimit.cs`, `src/MarketData.Sources/TwelveData/TwelveDataPriceSource.cs`
- Create: `tests/MarketData.Sources.Tests/MarketData.Sources.Tests.csproj`, `tests/MarketData.Sources.Tests/StubHandler.cs`
- Test: `tests/MarketData.Sources.Tests/TwelveDataPriceSourceTests.cs`

**Interfaces:**
- Consumes: `RawEnvelope`, `ContentHash`, `UrlRedactor` from Task 9.
- Produces: `IPriceSource` with `SourceId`, `Limits`, `FetchDailyBarsAsync(string symbol, DateOnly from, DateOnly to, CancellationToken) -> Task<RawEnvelope>`, `FetchSplitsAsync(string, CancellationToken)` and `FetchDividendsAsync(string, CancellationToken)`. Task 19 calls all three.

Raw datasets mirror the vendor's endpoints (`prices_daily`, `splits`, `dividends`); the curated model merges the latter two into `corporate_actions`.

- [ ] **Step 1: Create the projects**

```bash
dotnet new classlib -o src/MarketData.Sources -f net10.0
dotnet new xunit3 -o tests/MarketData.Sources.Tests -f net10.0
rm -f src/MarketData.Sources/Class1.cs tests/MarketData.Sources.Tests/UnitTest1.cs
dotnet sln add src/MarketData.Sources tests/MarketData.Sources.Tests
dotnet add src/MarketData.Sources reference src/MarketData.Domain src/MarketData.Storage
dotnet add tests/MarketData.Sources.Tests reference src/MarketData.Sources
dotnet add tests/MarketData.Sources.Tests package Shouldly
dotnet add tests/MarketData.Sources.Tests package Microsoft.Extensions.TimeProvider.Testing
```
Strip the `Version=` attributes.

- [ ] **Step 2: Write `StubHandler.cs`**

```csharp
using System.Net;

namespace MarketData.Sources.Tests;

/// <summary>Captures the request URI and returns a canned body. No network.</summary>
public sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
    : HttpMessageHandler
{
    public Uri? LastRequestUri { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;

        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body)
        });
    }
}
```

- [ ] **Step 3: Write the failing tests**

```csharp
using Microsoft.Extensions.Time.Testing;
using MarketData.Sources.TwelveData;
using Shouldly;

namespace MarketData.Sources.Tests;

public sealed class TwelveDataPriceSourceTests
{
    private const string Body =
        """{"meta":{"symbol":"AAPL"},"values":[{"datetime":"2020-08-31","open":"127.58","high":"131.00","low":"126.00","close":"129.04","volume":"225702700"}]}""";

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 22, 15, 3, TimeSpan.Zero);

    private static (TwelveDataPriceSource Source, StubHandler Handler) Build(string body = Body)
    {
        var handler = new StubHandler(body);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.twelvedata.com/") };
        var time = new FakeTimeProvider(Now);

        return (new TwelveDataPriceSource(client, "SECRET", time), handler);
    }

    [Fact]
    public async Task Stamps_observed_at_from_the_injected_clock()
    {
        var (source, _) = Build();

        var envelope = await source.FetchDailyBarsAsync(
            "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), TestContext.Current.CancellationToken);

        envelope.ObservedAt.ShouldBe(Now);
        envelope.ObservedAt.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task Stores_the_payload_verbatim_and_hashes_it()
    {
        var (source, _) = Build();

        var envelope = await source.FetchDailyBarsAsync(
            "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), TestContext.Current.CancellationToken);

        envelope.Payload.ShouldBe(Body);
        envelope.ContentHash.ShouldBe(Storage.ContentHash.Sha256(Body));
        envelope.Dataset.ShouldBe("prices_daily");
        envelope.SourceId.ShouldBe("twelvedata");
    }

    [Fact]
    public async Task Never_records_the_api_key()
    {
        var (source, handler) = Build();

        var envelope = await source.FetchDailyBarsAsync(
            "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), TestContext.Current.CancellationToken);

        handler.LastRequestUri!.Query.ShouldContain("SECRET");   // the real call carries it
        envelope.RequestUrl.ShouldNotContain("SECRET");          // the stored envelope does not
        envelope.RequestUrl.ShouldContain("apikey=REDACTED");
    }

    [Fact]
    public async Task Requests_only_the_window_asked_for()
    {
        var (source, handler) = Build();

        await source.FetchDailyBarsAsync(
            "AAPL", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 8), TestContext.Current.CancellationToken);

        handler.LastRequestUri!.Query.ShouldContain("start_date=2026-09-01");
        handler.LastRequestUri.Query.ShouldContain("end_date=2026-09-08");
    }

    [Fact]
    public async Task Splits_and_dividends_use_their_own_raw_datasets()
    {
        var (source, _) = Build("""{"splits":[]}""");

        var splits = await source.FetchSplitsAsync("AAPL", TestContext.Current.CancellationToken);
        var dividends = await source.FetchDividendsAsync("AAPL", TestContext.Current.CancellationToken);

        splits.Dataset.ShouldBe("splits");
        dividends.Dataset.ShouldBe("dividends");
    }

    [Fact]
    public async Task Throws_on_a_vendor_error_status()
    {
        var handler = new StubHandler("""{"code":429,"message":"limit"}""", System.Net.HttpStatusCode.TooManyRequests);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.twelvedata.com/") };
        var source = new TwelveDataPriceSource(client, "SECRET", new FakeTimeProvider(Now));

        await Should.ThrowAsync<HttpRequestException>(() => source.FetchSplitsAsync("AAPL", TestContext.Current.CancellationToken));
    }
}
```

- [ ] **Step 4: Run to verify it fails**

Run: `dotnet test tests/MarketData.Sources.Tests`
Expected: FAIL — `TwelveDataPriceSource` does not exist.

- [ ] **Step 5: Write `RateLimit.cs` and `IPriceSource.cs`**

```csharp
namespace MarketData.Sources;

/// <summary>Vendor quota. Both limits bind; the per-minute one is usually the tighter.</summary>
public sealed record RateLimit(int RequestsPerDay, int RequestsPerMinute);
```

```csharp
using MarketData.Storage;

namespace MarketData.Sources;

/// <summary>
/// Fetches vendor payloads and wraps them in an immutable envelope. The envelope's
/// <c>ObservedAt</c> is stamped here, exactly once, and copied by everything downstream.
/// </summary>
public interface IPriceSource
{
    string SourceId { get; }

    RateLimit Limits { get; }

    Task<RawEnvelope> FetchDailyBarsAsync(string symbol, DateOnly from, DateOnly to, CancellationToken ct);

    Task<RawEnvelope> FetchSplitsAsync(string symbol, CancellationToken ct);

    Task<RawEnvelope> FetchDividendsAsync(string symbol, CancellationToken ct);
}
```

- [ ] **Step 6: Write `TwelveData/TwelveDataPriceSource.cs`**

```csharp
using MarketData.Storage;

namespace MarketData.Sources.TwelveData;

/// <inheritdoc />
public sealed class TwelveDataPriceSource(
    HttpClient http,
    string apiKey,
    TimeProvider time) : IPriceSource
{
    public string SourceId => "twelvedata";

    /// <summary>Free tier: 800 credits/day, 8 credits/minute (verified 2026-09-08).</summary>
    public RateLimit Limits => new(RequestsPerDay: 800, RequestsPerMinute: 8);

    public Task<RawEnvelope> FetchDailyBarsAsync(
        string symbol, DateOnly from, DateOnly to, CancellationToken ct) =>
        FetchAsync(
            symbol,
            dataset: "prices_daily",
            path: $"time_series?symbol={Uri.EscapeDataString(symbol)}&interval=1day&outputsize=5000" +
                  $"&start_date={from:yyyy-MM-dd}&end_date={to:yyyy-MM-dd}",
            ct);

    public Task<RawEnvelope> FetchSplitsAsync(string symbol, CancellationToken ct) =>
        FetchAsync(symbol, "splits", $"splits?symbol={Uri.EscapeDataString(symbol)}&range=full", ct);

    public Task<RawEnvelope> FetchDividendsAsync(string symbol, CancellationToken ct) =>
        FetchAsync(symbol, "dividends", $"dividends?symbol={Uri.EscapeDataString(symbol)}&range=full", ct);

    private async Task<RawEnvelope> FetchAsync(
        string symbol, string dataset, string path, CancellationToken ct)
    {
        var url = $"{path}&apikey={Uri.EscapeDataString(apiKey)}";

        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync(ct);

        // Stamped once, here. Nothing downstream reads a clock.
        var observedAt = time.GetUtcNow().ToUniversalTime();

        return new RawEnvelope(
            SourceId: SourceId,
            Symbol: symbol,
            Dataset: dataset,
            ObservedAt: observedAt,
            RequestUrl: UrlRedactor.Redact(new Uri(http.BaseAddress!, url).ToString()),
            ContentHash: ContentHash.Sha256(payload),
            Payload: payload);
    }
}
```

- [ ] **Step 7: Run the tests**

Run: `dotnet test tests/MarketData.Sources.Tests`
Expected: PASS — 6 tests.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Add Twelve Data price source stamping observed_at once

The API key reaches the wire but never the envelope; a test asserts
both halves of that, because raw storage is permanent and versioned."
```

---

### Task 17: Parse payloads and apply the observation policy

**Files:**
- Create: `src/MarketData.Sources/IPriceNormaliser.cs`, `src/MarketData.Sources/ParsedBar.cs`, `src/MarketData.Sources/TwelveData/TwelveDataPriceNormaliser.cs`, `src/MarketData.Domain/ObservationPolicy.cs`
- Test: `tests/MarketData.Sources.Tests/TwelveDataPriceNormaliserTests.cs`, `tests/MarketData.Domain.Tests/ObservationPolicyTests.cs`

**Interfaces:**
- Consumes: `RawEnvelope`, `IPublicationClock`, `DailyBar`.
- Produces: `IPriceNormaliser.Parse(RawEnvelope) -> IReadOnlyList<ParsedBar>` (pure, no timestamps); `ObservationPolicy.Decide(DateOnly effectiveDate, decimal close, decimal? knownClose, DateTimeOffset fetchedAt) -> ObservationDecision?` returning null when nothing new was learned. Task 19 composes both.

This is the heart of the temporal model. Parsing is deliberately separated from timestamping: parsing must be a pure function of the envelope so a rebuild reproduces it, while the timestamp rule needs prior knowledge.

**The policy, in full.** For each parsed bar:

| Prior state | Fetch timing | Result |
|---|---|---|
| Not known | within the live window of publication | `Observed` at the envelope's fetch instant |
| Not known | long after publication (backfill) | `Inferred` at the reconstructed publication instant |
| Known, different close | any | `Observed` at the envelope's fetch instant — a restatement is real news |
| Known, same close | any | **nothing emitted** — nothing was learned |

The last row is what stops a daily full-history payload from re-ingesting thousands of unchanged rows.

- [ ] **Step 1: Write the failing policy tests**

```csharp
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
```

The last test is the guardrail from the spec: without it, a vendor correcting a 2020 bar in 2027 would claim the corrected value was knowable in 2020.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/MarketData.Domain.Tests --filter ObservationPolicyTests`
Expected: FAIL — `ObservationPolicy` does not exist.

- [ ] **Step 3: Write `src/MarketData.Domain/ObservationPolicy.cs`**

```csharp
namespace MarketData.Domain;

/// <summary>What timestamp a newly seen value should carry, and why.</summary>
public sealed record ObservationDecision(DateTimeOffset ObservedAt, ObservationKind Kind);

/// <summary>
/// Decides the <c>observed_at</c> of a value. Backfilled history has no true
/// observation instant, so its publication time is reconstructed; anything learned
/// close to publication, and every restatement, carries the real fetch instant.
/// </summary>
public sealed class ObservationPolicy(IPublicationClock clock, TimeSpan liveWindow)
{
    /// <summary>
    /// Returns null when nothing was learned — the value is already known and unchanged.
    /// </summary>
    public ObservationDecision? Decide(
        DateOnly effectiveDate,
        decimal close,
        decimal? knownClose,
        DateTimeOffset fetchedAt)
    {
        var fetched = fetchedAt.ToUniversalTime();

        if (knownClose is { } known)
        {
            // A restatement is genuine news, learned now. Never inferred: inferring
            // here would claim a corrected value was knowable at the original date.
            return known == close ? null : new ObservationDecision(fetched, ObservationKind.Observed);
        }

        var published = clock.InferredPublication(effectiveDate);

        return fetched - published <= liveWindow
            ? new ObservationDecision(fetched, ObservationKind.Observed)
            : new ObservationDecision(published, ObservationKind.Inferred);
    }
}
```

- [ ] **Step 4: Run the policy tests**

Run: `dotnet test tests/MarketData.Domain.Tests --filter ObservationPolicyTests`
Expected: PASS — 4 tests. The Task 1 architecture tests must still pass; this added no packages to Domain.

- [ ] **Step 5: Write the failing parser tests**

```csharp
using MarketData.Sources.TwelveData;
using MarketData.Storage;
using Shouldly;

namespace MarketData.Sources.Tests;

public sealed class TwelveDataPriceNormaliserTests
{
    private const string Body =
        """
        {"meta":{"symbol":"AAPL","currency":"USD"},
         "values":[
           {"datetime":"2020-09-01","open":"132.76","high":"134.80","low":"130.53","close":"134.18","volume":"151948100"},
           {"datetime":"2020-08-31","open":"127.58","high":"131.00","low":"126.00","close":"129.04","volume":"225702700"}
         ]}
        """;

    private static RawEnvelope Envelope(string payload = Body) => new(
        "twelvedata", "AAPL", "prices_daily",
        new DateTimeOffset(2026, 9, 8, 22, 15, 3, TimeSpan.Zero),
        "https://api.twelvedata.com/time_series?symbol=AAPL&apikey=REDACTED",
        ContentHash.Sha256(payload), payload);

    private readonly TwelveDataPriceNormaliser _normaliser = new();

    [Fact]
    public void Parses_every_bar_with_exact_decimals()
    {
        var bars = _normaliser.Parse(Envelope());

        bars.Count.ShouldBe(2);
        bars.ShouldContain(b => b.EffectiveDate == new DateOnly(2020, 8, 31) && b.Close == 129.04m);
        bars.ShouldContain(b => b.EffectiveDate == new DateOnly(2020, 9, 1) && b.Volume == 151_948_100L);
    }

    [Fact]
    public void Returns_bars_in_ascending_date_order_regardless_of_payload_order()
    {
        var bars = _normaliser.Parse(Envelope());

        bars.Select(b => b.EffectiveDate).ShouldBe(bars.Select(b => b.EffectiveDate).Order());
    }

    [Fact]
    public void Is_a_pure_function_of_the_envelope()
    {
        var envelope = Envelope();

        _normaliser.Parse(envelope).ShouldBe(_normaliser.Parse(envelope));
    }

    [Fact]
    public void Returns_nothing_for_a_payload_with_no_values()
    {
        _normaliser.Parse(Envelope("""{"status":"error","message":"no data"}""")).ShouldBeEmpty();
    }
}
```

Ordering is asserted because the observation policy is applied in sequence, and the rebuild-determinism guarantee depends on that sequence being stable.

- [ ] **Step 6: Write `ParsedBar.cs`, `IPriceNormaliser.cs` and the normaliser**

```csharp
namespace MarketData.Sources;

/// <summary>A bar exactly as the vendor stated it, before any timestamp is decided.</summary>
public sealed record ParsedBar(
    DateOnly EffectiveDate,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    string Currency);
```

```csharp
using MarketData.Storage;

namespace MarketData.Sources;

/// <summary>
/// Turns a raw envelope into bars. Deliberately pure: no clock, no I/O, no prior
/// state, so re-running it over the raw store reproduces the same result forever.
/// </summary>
public interface IPriceNormaliser
{
    string SourceId { get; }

    IReadOnlyList<ParsedBar> Parse(RawEnvelope raw);
}
```

```csharp
using System.Globalization;
using System.Text.Json;
using MarketData.Storage;

namespace MarketData.Sources.TwelveData;

/// <inheritdoc />
public sealed class TwelveDataPriceNormaliser : IPriceNormaliser
{
    public string SourceId => "twelvedata";

    public IReadOnlyList<ParsedBar> Parse(RawEnvelope raw)
    {
        using var document = JsonDocument.Parse(raw.Payload);

        if (!document.RootElement.TryGetProperty("values", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var currency = document.RootElement.TryGetProperty("meta", out var meta)
                       && meta.TryGetProperty("currency", out var c)
            ? c.GetString() ?? "USD"
            : "USD";

        return values.EnumerateArray()
            .Select(v => new ParsedBar(
                DateOnly.ParseExact(Text(v, "datetime"), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Dec(v, "open"), Dec(v, "high"), Dec(v, "low"), Dec(v, "close"),
                long.Parse(Text(v, "volume"), CultureInfo.InvariantCulture),
                currency))
            .OrderBy(b => b.EffectiveDate)
            .ToList();
    }

    private static string Text(JsonElement element, string name) =>
        element.GetProperty(name).GetString()
        ?? throw new InvalidDataException($"Property '{name}' was null.");

    // Vendor values arrive as exact decimal strings such as "328.31000".
    // Parsing to decimal preserves them; double would not.
    private static decimal Dec(JsonElement element, string name) =>
        decimal.Parse(Text(element, name), NumberStyles.Number, CultureInfo.InvariantCulture);
}
```

- [ ] **Step 7: Run the tests**

Run: `dotnet test tests/MarketData.Sources.Tests --filter TwelveDataPriceNormaliserTests`
Expected: PASS — 4 tests.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Add pure payload parsing and the observation policy

Parsing takes no clock and no prior state so a rebuild reproduces it.
The timestamp decision lives separately, where prior knowledge belongs,
and refuses to infer a restatement's timestamp."
```

---

### Task 18: Corporate actions parsing

**Files:**
- Create: `src/MarketData.Sources/ParsedAction.cs`, `src/MarketData.Sources/TwelveData/TwelveDataActionsNormaliser.cs`
- Test: `tests/MarketData.Sources.Tests/TwelveDataActionsNormaliserTests.cs`

**Interfaces:**
- Consumes: `RawEnvelope`.
- Produces: `TwelveDataActionsNormaliser.ParseSplits(RawEnvelope) -> IReadOnlyList<ParsedAction>` and `ParseDividends(RawEnvelope) -> IReadOnlyList<ParsedAction>`. Task 19 stamps these using the ex-date as the inferred instant.

An action's inferred `observed_at` is its **ex-date**, which under-claims knowledge — the announcement came weeks earlier. Erring toward not knowing is the safe direction, and it makes adjustment self-consistent: a query as at the day before an ex-date does not know about the split, so it returns the unadjusted prices that were actually quoted that day.

- [ ] **Step 1: Write the failing tests**

```csharp
using MarketData.Domain;
using MarketData.Sources.TwelveData;
using MarketData.Storage;
using Shouldly;

namespace MarketData.Sources.Tests;

public sealed class TwelveDataActionsNormaliserTests
{
    private const string SplitsBody =
        """
        {"meta":{"symbol":"AAPL","currency":"USD"},
         "splits":[
           {"date":"2020-08-31","description":"4-for-1 split","ratio":0.25,"from_factor":4,"to_factor":1},
           {"date":"2014-06-09","description":"7-for-1 split","ratio":0.14286,"from_factor":7,"to_factor":1}
         ]}
        """;

    private const string DividendsBody =
        """
        {"meta":{"symbol":"AAPL","currency":"USD"},
         "dividends":[{"ex_date":"2026-08-10","amount":0.27},{"ex_date":"2026-05-11","amount":0.27}]}
        """;

    private static RawEnvelope Envelope(string dataset, string payload) => new(
        "twelvedata", "AAPL", dataset,
        new DateTimeOffset(2026, 9, 8, 22, 15, 3, TimeSpan.Zero),
        "https://api.twelvedata.com/x?apikey=REDACTED",
        ContentHash.Sha256(payload), payload);

    private readonly TwelveDataActionsNormaliser _normaliser = new();

    [Fact]
    public void Parses_the_aapl_four_for_one_split()
    {
        var actions = _normaliser.ParseSplits(Envelope("splits", SplitsBody));

        var split = actions.Single(a => a.ExDate == new DateOnly(2020, 8, 31));
        split.ActionType.ShouldBe(CorporateActionType.Split);
        split.Ratio.ShouldBe(0.25m);
        split.Amount.ShouldBeNull();
    }

    [Fact]
    public void Parses_dividends_with_amount_and_no_ratio()
    {
        var actions = _normaliser.ParseDividends(Envelope("dividends", DividendsBody));

        var dividend = actions.Single(a => a.ExDate == new DateOnly(2026, 8, 10));
        dividend.ActionType.ShouldBe(CorporateActionType.Dividend);
        dividend.Amount.ShouldBe(0.27m);
        dividend.Ratio.ShouldBeNull();
    }

    [Fact]
    public void Returns_actions_in_ascending_ex_date_order()
    {
        var actions = _normaliser.ParseSplits(Envelope("splits", SplitsBody));

        actions.Select(a => a.ExDate).ShouldBe(actions.Select(a => a.ExDate).Order());
    }

    [Fact]
    public void Returns_nothing_for_a_symbol_with_no_actions()
    {
        _normaliser.ParseSplits(Envelope("splits", """{"splits":[]}""")).ShouldBeEmpty();
        _normaliser.ParseDividends(Envelope("dividends", """{"dividends":[]}""")).ShouldBeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/MarketData.Sources.Tests --filter TwelveDataActionsNormaliserTests`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Write `ParsedAction.cs`**

```csharp
using MarketData.Domain;

namespace MarketData.Sources;

/// <summary>A corporate action as the vendor stated it, before any timestamp is decided.</summary>
public sealed record ParsedAction(
    DateOnly ExDate,
    CorporateActionType ActionType,
    decimal? Ratio,
    decimal? Amount,
    string Currency);
```

- [ ] **Step 4: Write `TwelveData/TwelveDataActionsNormaliser.cs`**

```csharp
using System.Globalization;
using System.Text.Json;
using MarketData.Domain;
using MarketData.Storage;

namespace MarketData.Sources.TwelveData;

/// <summary>Parses Twelve Data's split and dividend payloads into a single action model.</summary>
public sealed class TwelveDataActionsNormaliser
{
    public string SourceId => "twelvedata";

    public IReadOnlyList<ParsedAction> ParseSplits(RawEnvelope raw) =>
        Parse(raw, "splits", "date", (element, currency) => new ParsedAction(
            Date(element, "date"),
            CorporateActionType.Split,
            Ratio: Dec(element, "ratio"),
            Amount: null,
            currency));

    public IReadOnlyList<ParsedAction> ParseDividends(RawEnvelope raw) =>
        Parse(raw, "dividends", "ex_date", (element, currency) => new ParsedAction(
            Date(element, "ex_date"),
            CorporateActionType.Dividend,
            Ratio: null,
            Amount: Dec(element, "amount"),
            currency));

    private static IReadOnlyList<ParsedAction> Parse(
        RawEnvelope raw,
        string arrayName,
        string dateField,
        Func<JsonElement, string, ParsedAction> map)
    {
        using var document = JsonDocument.Parse(raw.Payload);

        if (!document.RootElement.TryGetProperty(arrayName, out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var currency = document.RootElement.TryGetProperty("meta", out var meta)
                       && meta.TryGetProperty("currency", out var c)
            ? c.GetString() ?? "USD"
            : "USD";

        return items.EnumerateArray()
            .Select(item => map(item, currency))
            .OrderBy(a => a.ExDate)
            .ToList();
    }

    private static DateOnly Date(JsonElement element, string name) =>
        DateOnly.ParseExact(
            element.GetProperty(name).GetString()
            ?? throw new InvalidDataException($"Property '{name}' was null."),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture);

    // Numbers arrive unquoted here, unlike the price endpoints. GetDecimal keeps
    // full precision; a double round-trip would not.
    private static decimal Dec(JsonElement element, string name) =>
        element.GetProperty(name).GetDecimal();
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/MarketData.Sources.Tests --filter TwelveDataActionsNormaliserTests`
Expected: PASS — 4 tests.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add corporate action parsing for splits and dividends

Split ratios and dividend amounts arrive as JSON numbers rather than
strings, so they are read with GetDecimal to keep full precision."
```

---

### Task 19: Ingest orchestration

**Files:**
- Create: `src/MarketData.Sources/IngestService.cs`, `src/MarketData.Sources/IngestResult.cs`
- Test: `tests/MarketData.Sources.Tests/IngestServiceTests.cs`

**Interfaces:**
- Consumes: everything built so far — `IPriceSource`, `IPriceNormaliser`, `ObservationPolicy`, `IRawStore`, `ICuratedStore`, `ICursorRepository`.
- Produces: `IngestService.IngestSymbolAsync(string symbol, DateOnly from, DateOnly to, string ingestId, CancellationToken) -> Task<IngestResult>`. The Lambda and CLI in the next plan both call exactly this.

This is where the content-hash short-circuit and the cursor advance live. Prior knowledge for the observation policy comes from the cursor's recorded close values.

- [ ] **Step 1: Write the failing tests**

```csharp
using MarketData.Domain;
using MarketData.Sources;
using MarketData.Sources.TwelveData;
using MarketData.Storage;
using MarketData.Storage.Local;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace MarketData.Sources.Tests;

public sealed class IngestServiceTests : IDisposable
{
    private const string Body =
        """{"meta":{"symbol":"AAPL","currency":"USD"},"values":[{"datetime":"2020-08-31","open":"127.58","high":"131.00","low":"126.00","close":"129.04","volume":"225702700"}]}""";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pitmd-" + Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 22, 15, 3, TimeSpan.Zero);

    private IngestService Build(string body, out InMemoryCursorRepository cursors)
    {
        var handler = new StubHandler(body);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.twelvedata.com/") };
        var source = new TwelveDataPriceSource(client, "SECRET", new FakeTimeProvider(Now));

        cursors = new InMemoryCursorRepository();

        return new IngestService(
            source,
            new TwelveDataPriceNormaliser(),
            new ObservationPolicy(new UsEquityPublicationClock(), TimeSpan.FromHours(24)),
            new LocalRawStore(_root),
            new LocalCuratedStore(_root),
            cursors);
    }

    [Fact]
    public async Task First_run_writes_raw_and_curated_and_advances_the_cursor()
    {
        var service = Build(Body, out var cursors);

        var result = await service.IngestSymbolAsync(
            "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), "run-1", TestContext.Current.CancellationToken);

        result.RowsWritten.ShouldBe(1);
        result.SkippedUnchanged.ShouldBeFalse();
        result.RawKey.ShouldNotBeNull();

        var cursor = await cursors.GetAsync("prices_daily", "AAPL", TestContext.Current.CancellationToken);
        cursor!.LastEffectiveDate.ShouldBe(new DateOnly(2020, 8, 31));
        cursor.LastContentHash.ShouldBe(ContentHash.Sha256(Body));
    }

    [Fact]
    public async Task Identical_payload_short_circuits_without_writing_anything()
    {
        var service = Build(Body, out _);
        await service.IngestSymbolAsync("AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), "run-1", TestContext.Current.CancellationToken);

        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Length;

        var second = await service.IngestSymbolAsync(
            "AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), "run-2", TestContext.Current.CancellationToken);

        second.SkippedUnchanged.ShouldBeTrue();
        second.RowsWritten.ShouldBe(0);
        Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Length.ShouldBe(before);
    }

    [Fact]
    public async Task Backfilled_bars_are_tagged_inferred()
    {
        var service = Build(Body, out _);

        await service.IngestSymbolAsync("AAPL", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), "run-1", TestContext.Current.CancellationToken);

        using var duck = new StorageDuck(_root);
        duck.Single("SELECT ObservedAtKind FROM ").ShouldBe("INFERRED");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

/// <summary>Cursor repository backed by a dictionary, so these tests need no Docker.</summary>
public sealed class InMemoryCursorRepository : Storage.Dynamo.ICursorRepository
{
    private readonly Dictionary<string, Storage.Dynamo.Cursor> _cursors = [];

    public Task<Storage.Dynamo.Cursor?> GetAsync(string dataset, string symbol, CancellationToken ct) =>
        Task.FromResult(_cursors.GetValueOrDefault($"{dataset}#{symbol}"));

    public Task SaveAsync(Storage.Dynamo.Cursor cursor, CancellationToken ct)
    {
        _cursors[$"{cursor.Dataset}#{cursor.Symbol}"] = cursor;
        return Task.CompletedTask;
    }
}
```

Add a small DuckDB helper to this project mirroring `DuckDbReader` from Task 12:

```csharp
using DuckDB.NET.Data;

namespace MarketData.Sources.Tests;

/// <summary>Reads curated Parquet under a root, as the query layer will.</summary>
public sealed class StorageDuck(string root) : IDisposable
{
    private readonly DuckDBConnection _connection = Open();

    private static DuckDBConnection Open()
    {
        var c = new DuckDBConnection("DataSource=:memory:");
        c.Open();
        return c;
    }

    public string Single(string selectPrefix)
    {
        var glob = Path.Combine(root, "curated", "dataset=prices_daily", "**", "*.parquet")
            .Replace('\\', '/');

        using var command = _connection.CreateCommand();
        command.CommandText = $"{selectPrefix}read_parquet('{glob}')";

        using var reader = command.ExecuteReader();
        reader.Read();

        return reader.GetValue(0).ToString()!;
    }

    public void Dispose() => _connection.Dispose();
}
```

Add the required package references:

```bash
dotnet add tests/MarketData.Sources.Tests package DuckDB.NET.Data.Full
dotnet add src/MarketData.Sources reference src/MarketData.Storage
```
Strip the `Version=` attribute.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/MarketData.Sources.Tests --filter IngestServiceTests`
Expected: FAIL — `IngestService` does not exist.

- [ ] **Step 3: Write `IngestResult.cs`**

```csharp
namespace MarketData.Sources;

/// <summary>Outcome of ingesting one symbol, recorded against the run.</summary>
public sealed record IngestResult(
    string Symbol,
    bool SkippedUnchanged,
    int RowsWritten,
    string? RawKey,
    IReadOnlyList<string> CuratedKeys);
```

- [ ] **Step 4: Write `IngestService.cs`**

```csharp
using MarketData.Domain;
using MarketData.Storage;
using MarketData.Storage.Dynamo;

namespace MarketData.Sources;

/// <summary>
/// Fetches one symbol, short-circuits if the vendor payload is unchanged, applies
/// the observation policy, and appends only what was actually learned.
/// </summary>
public sealed class IngestService(
    IPriceSource source,
    IPriceNormaliser normaliser,
    ObservationPolicy policy,
    IRawStore rawStore,
    ICuratedStore curatedStore,
    ICursorRepository cursors)
{
    private const string Dataset = "prices_daily";

    public async Task<IngestResult> IngestSymbolAsync(
        string symbol, DateOnly from, DateOnly to, string ingestId, CancellationToken ct)
    {
        var envelope = await source.FetchDailyBarsAsync(symbol, from, to, ct);
        var cursor = await cursors.GetAsync(Dataset, symbol, ct);

        // Nothing changed at the vendor, so nothing was learned. No raw object is
        // written: an identical payload is not a new observation.
        if (cursor?.LastContentHash == envelope.ContentHash)
        {
            return new IngestResult(symbol, SkippedUnchanged: true, 0, null, []);
        }

        var runDate = DateOnly.FromDateTime(envelope.ObservedAt.UtcDateTime);
        var rawKey = await rawStore.WriteAsync(envelope, runDate, ct);

        var parsed = normaliser.Parse(envelope);
        var bars = new List<DailyBar>(parsed.Count);

        foreach (var bar in parsed)
        {
            // Prior knowledge is currently the cursor's watermark only; a bar at or
            // before it is treated as already known and unchanged. Task 20 of the
            // next plan replaces this with a per-date lookup through the query layer.
            var known = cursor?.LastEffectiveDate is { } last && bar.EffectiveDate <= last
                ? bar.Close
                : (decimal?)null;

            if (policy.Decide(bar.EffectiveDate, bar.Close, known, envelope.ObservedAt) is not { } decision)
            {
                continue;
            }

            bars.Add(new DailyBar(
                symbol, bar.EffectiveDate,
                bar.Open, bar.High, bar.Low, bar.Close, bar.Volume,
                bar.Currency, source.SourceId,
                decision.ObservedAt, decision.Kind,
                ingestId, rawKey));
        }

        var curatedKeys = bars.Count > 0
            ? await curatedStore.AppendPricesAsync(bars, ingestId, ct)
            : [];

        await cursors.SaveAsync(
            new Cursor(
                Dataset,
                symbol,
                parsed.Count > 0 ? parsed[^1].EffectiveDate : cursor?.LastEffectiveDate,
                envelope.ContentHash),
            ct);

        return new IngestResult(symbol, SkippedUnchanged: false, bars.Count, rawKey, curatedKeys);
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/MarketData.Sources.Tests --filter IngestServiceTests`
Expected: PASS — 3 tests.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test --filter "Category!=Integration"`
Expected: PASS — every unit test.
Run: `dotnet test` (Docker running)
Expected: PASS — including LocalStack integration tests.

- [ ] **Step 7: Commit and push**

```bash
git add -A
git commit -m "Add ingest orchestration with hash short-circuit

An identical vendor payload writes no raw object at all: nothing was
learned, so there is nothing to observe. Only bars the observation
policy accepts reach curated storage."
git push
gh run list --limit 1
```

Expected: CI concludes `success`.

---

## Definition of done for this plan

- `dotnet test` passes with Docker running; `dotnet test --filter "Category!=Integration"` passes without it.
- `terraform apply` has been run and two billing alarms exist in `us-east-1`.
- A real Twelve Data key is stored in SSM as a `SecureString`, and `scripts/verify-vendor.sh` reports three passes.
- `IngestService` writes raw JSON and curated Parquet for one symbol, and re-running the same fetch writes nothing.
- No API key appears anywhere under `raw/`.
- Instrument reference data round-trips through DynamoDB with `cik` preserved.
- The Domain project still has zero package references.

## Deliberately deferred to the next plan

These are **not** gaps — they are Stages 4–6 of the spec, and each is named here so an executor does not build them early:

- The as-of query layer (`IMarketDataQuery`), adjustment calculation, and the eight temporal tests.
- Replacing the cursor-watermark approximation of prior knowledge in `IngestService` with a real per-date lookup through the query layer. Until then, a restatement of a bar at or before the watermark is detected only when its close differs, which is the common case but not every case.
- The Lambda handler, its IAM role, EventBridge schedule, and log retention.
- The CLI (`watchlist`, `backfill`, `query`, `reprocess`, `compact`).
- Run records mirrored to S3, and GitHub OIDC for Terraform plans in CI.
