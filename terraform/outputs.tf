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
  description = "The public URL of the Atoll API (ALB DNS name)"
  value       = "http://${aws_lb.main.dns_name}"
}
