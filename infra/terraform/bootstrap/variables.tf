variable "project" {
  description = "Project name prefix for all resources."
  type        = string
  default     = "pit-marketdata"
}

variable "region" {
  description = "AWS region."
  type        = string
  default     = "ap-southeast-2"
}

variable "account_suffix" {
  description = "Short unique suffix making the state bucket name globally unique."
  type        = string
}
