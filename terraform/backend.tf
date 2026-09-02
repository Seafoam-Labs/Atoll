terraform {
  required_version = ">= 1.10.0"

  # Must match the bucket created by the bootstrap stack (terraform/bootstrap).
  backend "s3" {
    bucket       = "seafoam-atoll-tfstate"
    key          = "atoll-api/terraform.tfstate"
    region       = "us-east-1"
    use_lockfile = true
  }
}
