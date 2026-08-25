# HTTP API Gateway (v2) for API-style HTTP traffic.
#
# This endpoint runs in parallel with the public ALB. Browser and Blazor Server
# traffic must use the ALB-backed application domain because API Gateway HTTP
# APIs do not transparently proxy SignalR WebSocket upgrades.

# Security group attached to the API Gateway VPC Link ENIs. The ALB security
# group permits port 80 from this group only for private integration traffic.
resource "aws_security_group" "apigw_vpc_link" {
  name        = "${var.project_name}-apigw-vpc-link-sg"
  description = "SG for the API Gateway VPC Link ENIs"
  vpc_id      = data.aws_vpc.default.id

  tags = {
    Name = "${var.project_name}-apigw-vpc-link-sg"
  }
}

# Declared separately to avoid a dependency cycle between the VPC Link and ALB
# security groups.
resource "aws_vpc_security_group_egress_rule" "apigw_vpc_link_to_alb" {
  description                  = "HTTP to the ALB API listener"
  security_group_id            = aws_security_group.apigw_vpc_link.id
  referenced_security_group_id = aws_security_group.alb_sg.id
  from_port                    = 8080
  to_port                      = 8080
  ip_protocol                  = "tcp"
}

resource "aws_apigatewayv2_vpc_link" "main" {
  name               = "${var.project_name}-vpc-link"
  security_group_ids = [aws_security_group.apigw_vpc_link.id]
  subnet_ids         = data.aws_subnets.default.ids

  tags = {
    Name = "${var.project_name}-vpc-link"
  }
}

resource "aws_apigatewayv2_api" "main" {
  name          = "${var.project_name}-http-api"
  protocol_type = "HTTP"
  description   = "HTTP API Gateway for ${var.project_name} API traffic"
}

resource "aws_apigatewayv2_integration" "alb" {
  api_id             = aws_apigatewayv2_api.main.id
  integration_type   = "HTTP_PROXY"
  integration_method = "ANY"
  integration_uri    = aws_lb_listener.api_gateway.arn

  connection_type        = "VPC_LINK"
  connection_id          = aws_apigatewayv2_vpc_link.main.id
  payload_format_version = "1.0"
  timeout_milliseconds   = 30000
}

resource "aws_apigatewayv2_route" "proxy" {
  api_id    = aws_apigatewayv2_api.main.id
  route_key = "ANY /{proxy+}"
  target    = "integrations/${aws_apigatewayv2_integration.alb.id}"
}

resource "aws_apigatewayv2_route" "root" {
  api_id    = aws_apigatewayv2_api.main.id
  route_key = "ANY /"
  target    = "integrations/${aws_apigatewayv2_integration.alb.id}"
}

resource "aws_apigatewayv2_stage" "default" {
  api_id      = aws_apigatewayv2_api.main.id
  name        = "$default"
  auto_deploy = true
}
