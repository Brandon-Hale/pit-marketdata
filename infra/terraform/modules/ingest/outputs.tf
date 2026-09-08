output "function_name" {
  description = "Name of the ingest function."
  value       = aws_lambda_function.ingest.function_name
}

output "function_arn" {
  description = "ARN of the ingest function."
  value       = aws_lambda_function.ingest.arn
}

output "log_group" {
  description = "CloudWatch log group receiving function logs."
  value       = aws_cloudwatch_log_group.ingest.name
}
