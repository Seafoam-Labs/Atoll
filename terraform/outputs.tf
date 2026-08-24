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
  description = "The public URL of the Atoll API (API Gateway custom domain)"
  value       = "https://${var.api_domain_name}"
}

output "api_gateway_invoke_url" {
  description = "Default execute-api URL of the HTTP API Gateway (useful for debugging)"
  value       = aws_apigatewayv2_api.main.api_endpoint
}

output "api_gateway_target_domain_name" {
  description = "Regional target of the API Gateway custom domain; point the public CNAME for the domain here"
  value       = aws_apigatewayv2_domain_name.main.domain_name_configuration[0].target_domain_name
}


