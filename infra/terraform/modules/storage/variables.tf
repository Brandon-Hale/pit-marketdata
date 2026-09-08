variable "project" {
  description = "Project name prefix."
  type        = string
}

variable "bucket_suffix" {
  description = "Short unique suffix making the data bucket name globally unique."
  type        = string
}

variable "raw_ia_transition_days" {
  description = "Days before raw objects move to Infrequent Access."
  type        = number
  default     = 90
}
