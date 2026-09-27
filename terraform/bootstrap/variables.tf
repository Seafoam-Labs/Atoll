variable "aws_region" {
  description = "AWS region"
  default     = "us-east-1"
}

variable "project_name" {
  description = "Project name"
  default     = "atoll-api"
}

variable "github_repo" {
  description = "GitHub repository allowed to assume the deploy role (owner/repo)"
  default     = "Seafoam-Labs/Atoll"
}

variable "deploy_branch" {
  description = "Branch allowed to run terraform apply"
  default     = "main"
}

variable "state_bucket_name" {
  description = "Globally unique name for the Terraform state bucket"
  default     = "seafoam-atoll-tfstate"
}

# Must match topic_name in the Buoy stack, which owns the shared alert topic.
variable "alert_topic_name" {
  description = "SNS topic the main stack's alarms publish to"
  default     = "seafoam-alerts"
}
