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

## Why the suffix is random

S3 bucket names are globally unique, so the project name alone is not enough.
The suffix is deliberately **not** derived from the AWS account ID: this
repository is public and `backend.tf` is committed, so an account-derived
suffix would publish the account number.
