variable "project" {
  description = "Project name prefix."
  type        = string
}

variable "alarm_email" {
  description = "Address to receive billing alarm notifications."
  type        = string
}

variable "billing_thresholds_usd" {
  description = "Estimated-charge thresholds, in USD, that raise an alarm."
  type        = list(number)
  default     = [5, 20]
}
