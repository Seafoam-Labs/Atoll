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
  description = "The public URL of the Atoll API"
  value       = "https://${var.api_domain_name}"
}

output "api_gateway_invoke_url" {
  description = "Default execute-api URL for HTTP API clients; do not use it for Blazor WebSockets"
  value       = aws_apigatewayv2_api.main.api_endpoint
}

output "alb_dns_name" {
  description = "The DNS name of the Application Load Balancer; point the public CNAME (or Route 53 ALIAS) for the domain here"
  value       = aws_lb.main.dns_name
}

output "alb_zone_id" {
  description = "The canonical hosted zone ID of the Application Load Balancer (for Route 53 Alias records)"
  value       = aws_lb.main.zone_id
}


