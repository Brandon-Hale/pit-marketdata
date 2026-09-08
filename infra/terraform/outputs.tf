output "data_bucket" {
  description = "S3 bucket holding raw/ and curated/."
  value       = module.storage.data_bucket
}

output "table_name" {
  description = "DynamoDB state table."
  value       = module.storage.table_name
}

output "ingest_function_name" {
  description = "Name of the scheduled ingest function."
  value       = module.ingest.function_name
}

output "ingest_log_group" {
  description = "Where the scheduled function writes its logs."
  value       = module.ingest.log_group
}
