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
