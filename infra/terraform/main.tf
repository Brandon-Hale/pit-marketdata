terraform {
  required_version = ">= 1.14"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }
}

# Every resource this project creates carries these, so the whole footprint can be
# found — and costed — with a single tag filter. Applied via the providers'
# default_tags rather than per resource, so nothing can be added untagged.
locals {
  common_tags = {
    Project   = var.project
    ManagedBy = "terraform"
  }
}

provider "aws" {
  region = var.region

  default_tags {
    tags = local.common_tags
  }
}

provider "aws" {
  alias  = "us_east_1"
  region = "us-east-1"

  default_tags {
    tags = local.common_tags
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
