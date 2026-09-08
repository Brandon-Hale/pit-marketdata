output "alarm_topic_arn" {
  description = "SNS topic receiving billing alarms."
  value       = aws_sns_topic.alarms.arn
}
