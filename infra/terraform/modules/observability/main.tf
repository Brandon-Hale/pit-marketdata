terraform {
  required_providers {
    aws = {
      source                = "hashicorp/aws"
      configuration_aliases = [aws.us_east_1]
    }
  }
}

resource "aws_sns_topic" "alarms" {
  provider = aws.us_east_1
  name     = "${var.project}-billing-alarms"
}

resource "aws_sns_topic_subscription" "email" {
  provider  = aws.us_east_1
  topic_arn = aws_sns_topic.alarms.arn
  protocol  = "email"
  endpoint  = var.alarm_email
}

resource "aws_cloudwatch_metric_alarm" "billing" {
  provider = aws.us_east_1
  count    = length(var.billing_thresholds_usd)

  alarm_name          = "${var.project}-billing-over-${var.billing_thresholds_usd[count.index]}-usd"
  alarm_description   = "Estimated AWS charges exceeded USD ${var.billing_thresholds_usd[count.index]}."
  namespace           = "AWS/Billing"
  metric_name         = "EstimatedCharges"
  statistic           = "Maximum"
  period              = 21600
  evaluation_periods  = 1
  threshold           = var.billing_thresholds_usd[count.index]
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"

  dimensions = {
    Currency = "USD"
  }

  alarm_actions = [aws_sns_topic.alarms.arn]
}
