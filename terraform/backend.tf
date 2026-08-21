terraform {
  required_version = ">= 1.5.0"

  # Must match the resources created by the bootstrap stack (terraform/bootstrap).
  backend "s3" {
    bucket         = "seafoam-atoll-tfstate"
    key            = "atoll-api/terraform.tfstate"
    region         = "us-east-1"
    dynamodb_table = "atoll-api-terraform-locks"
  }
}
