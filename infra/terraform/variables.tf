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

variable "lambda_zip_path" {
  description = "Path to the built Lambda deployment package, from scripts/build-lambda.sh."
  type        = string
  default     = "../../dist/lambda.zip"
}
