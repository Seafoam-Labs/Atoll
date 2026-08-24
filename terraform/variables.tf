variable "aws_region" {
  description = "AWS region"
  default     = "us-east-1"
}

variable "project_name" {
  description = "Project name"
  default     = "atoll-api"
}

variable "container_port" {
  description = "Port exposed by the docker image"
  default     = 8080
}

variable "cpu" {
  description = "Fargate instance CPU units to provision (1 vCPU = 1024 CPU units)"
  default     = "512"
}

variable "memory" {
  description = "Fargate instance memory to provision (in MiB)"
  default     = "2048"
}

variable "image_tag" {
  description = "Container image tag to deploy (CI passes the git commit SHA)"
  default     = "latest"
}

variable "mongo_database" {
  description = "Application database name used in the DocumentDB connection string"
  default     = "atoll"
}

variable "docdb_instance_class" {
  description = "DocumentDB instance class (db.t4g.medium is the only T4G class DocumentDB offers)"
  default     = "db.t4g.medium"
}

variable "docdb_engine_version" {
  description = "DocumentDB engine version"
  default     = "8.0.1"
}

variable "docdb_master_username" {
  description = "DocumentDB master username"
  default     = "atollmaster"
}

variable "api_domain_name" {
  description = "Custom domain name registered in API Gateway that fronts the ALB"
  default     = "atoll.seafoam-labs.org"
}
