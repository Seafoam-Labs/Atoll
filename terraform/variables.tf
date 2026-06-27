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
