variable "project" {
  description = "Project name prefix."
  type        = string
}

variable "data_bucket" {
  description = "Name of the S3 bucket holding raw/ and curated/."
  type        = string
}

variable "data_bucket_arn" {
  description = "ARN of the data bucket."
  type        = string
}

variable "table_name" {
  description = "Name of the DynamoDB state table."
  type        = string
}

variable "table_arn" {
  description = "ARN of the DynamoDB table."
  type        = string
}

variable "region" {
  description = "AWS region for the function."
  type        = string
}

variable "lambda_zip_path" {
  description = "Path to the built deployment package, produced by scripts/build-lambda.sh."
  type        = string
}

variable "api_key_parameter" {
  description = "SSM parameter holding the vendor API key."
  type        = string
  default     = "/pit-marketdata/twelvedata/apikey"
}

variable "schedule_expression" {
  description = "When to run. Weekdays after the US close."
  type        = string
  default     = "cron(15 22 ? * MON-FRI *)"
}

variable "log_retention_days" {
  description = "CloudWatch log retention. The default is never expire, which costs money quietly."
  type        = number
  default     = 14
}

variable "reserved_concurrency" {
  description = <<-DESC
    Reserved concurrent executions, or -1 to leave the function unreserved.

    Two concurrent runs would both write raw objects and both advance the same cursor, so 1
    is the correct value. It is not the default because a new AWS account has a total
    concurrency limit of 10, and AWS refuses any reservation that would leave fewer than 10
    unreserved -- so setting it fails until the account quota is raised via Service Quotas
    ("Concurrent executions"). Set this to 1 once that is done.

    The exposure while unreserved is small: the schedule fires once per weekday and a run
    takes about a minute, so an overlap would need a retry to collide with a still-running
    invocation.
  DESC

  type    = number
  default = -1
}
