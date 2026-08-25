output "ecs_cluster_name" {
  description = "Name of the ECS Cluster"
  value       = aws_ecs_cluster.main.name
}

output "ecs_service_name" {
  description = "Name of the ECS Service"
  value       = aws_ecs_service.main.name
}

output "repository_url" {
  description = "The URL of the ECR repository"
  value       = data.aws_ecr_repository.app.repository_url
}

output "application_url" {
  description = "The public URL of the Atoll API (CloudFront custom domain)"
  value       = "https://${var.api_domain_name}"
}

output "cloudfront_domain_name" {
  description = "Domain name of the CloudFront distribution; point the public CNAME for the custom domain here"
  value       = aws_cloudfront_distribution.main.domain_name
}


