output "deploy_role_arn" {
  description = "Set this as the AWS_DEPLOY_ROLE_ARN repository variable in GitHub (Settings > Secrets and variables > Actions > Variables)"
  value       = aws_iam_role.github_deploy.arn
}

output "state_bucket" {
  description = "S3 bucket holding the main stack's Terraform state"
  value       = aws_s3_bucket.tfstate.bucket
}

output "oidc_provider_arn" {
  description = "GitHub OIDC identity provider registered in this account"
  value       = aws_iam_openid_connect_provider.github.arn
}

output "ecr_repository_url" {
  description = "ECR repository the pipeline pushes container images to"
  value       = aws_ecr_repository.app.repository_url
}
