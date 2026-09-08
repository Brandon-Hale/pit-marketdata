output "state_bucket" {
  description = "Name to use as the S3 backend bucket in the root configuration."
  value       = aws_s3_bucket.state.id
}
