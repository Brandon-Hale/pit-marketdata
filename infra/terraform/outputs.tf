output "data_bucket" {
  description = "S3 bucket holding raw/ and curated/."
  value       = module.storage.data_bucket
}

output "table_name" {
  description = "DynamoDB state table."
  value       = module.storage.table_name
}
