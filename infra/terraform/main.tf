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
