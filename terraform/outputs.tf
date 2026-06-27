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
  value       = aws_ecr_repository.app.repository_url
}

output "api_gateway_url" {
  description = "The URL of the API Gateway"
  value       = aws_apigatewayv2_stage.main.invoke_url
}
