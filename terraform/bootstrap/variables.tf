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

variable "lock_table_name" {
  description = "DynamoDB table used for Terraform state locking"
  default     = "atoll-api-terraform-locks"
}
