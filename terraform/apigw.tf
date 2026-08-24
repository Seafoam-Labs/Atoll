# HTTP API Gateway (v2) fronting the internal ALB.
#
# HTTP APIs proxy the `Upgrade: websocket` handshake transparently on
# ANY /{proxy+} integrations, so Blazor Server's SignalR circuit keeps a real
# WebSocket end-to-end (edge -> VPC Link -> ALB -> ECS task) instead of
# falling back to long polling.

# Security group attached to the API Gateway VPC Link ENIs. Referenced by the
# ALB SG (in main.tf) to allow ingress only from the VPC Link.
resource "aws_security_group" "apigw_vpc_link" {
  name        = "${var.project_name}-apigw-vpc-link-sg"
  description = "SG for the API Gateway VPC Link ENIs"
  vpc_id      = data.aws_vpc.default.id

  tags = {
    Name = "${var.project_name}-apigw-vpc-link-sg"
  }
}

# Egress rule declared standalone to break the SG reference cycle with the
# ALB SG (which allows ingress from this SG).
resource "aws_vpc_security_group_egress_rule" "apigw_vpc_link_to_alb" {
  description                  = "HTTP to the internal ALB"
  security_group_id            = aws_security_group.apigw_vpc_link.id
  referenced_security_group_id = aws_security_group.alb_sg.id
  from_port                    = 80
  to_port                      = 80
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

  # Blazor's SignalR sends cookies and non-trivial headers; forwarding all
  # methods and letting the app handle CORS keeps behavior identical to
  # hitting the ALB directly.
  description = "HTTP API Gateway in front of the ${var.project_name} ALB"
}

resource "aws_apigatewayv2_integration" "alb" {
  api_id             = aws_apigatewayv2_api.main.id
  integration_type   = "HTTP_PROXY"
  integration_method = "ANY"
  # Forward every request (any method, any path, including WebSocket upgrades)
  # to the ALB's HTTP listener. API Gateway appends the matched path.
  integration_uri = aws_lb_listener.http.arn

  connection_type = "VPC_LINK"
  connection_id   = aws_apigatewayv2_vpc_link.main.id

  payload_format_version = "1.0"

  # Blazor circuits can be idle for long stretches between server-sent updates
  # and client input; use the HTTP API maximum (30s) to reduce chances of
  # spurious 504s. Longer-lived idleness is still handled by the WebSocket
  # keep-alive at the SignalR layer.
  timeout_milliseconds = 30000
}

resource "aws_apigatewayv2_route" "proxy" {
  api_id    = aws_apigatewayv2_api.main.id
  route_key = "ANY /{proxy+}"
  target    = "integrations/${aws_apigatewayv2_integration.alb.id}"
}

# Route requests to `/` (no proxy match) to the same integration.
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

# The custom domain `atoll.seafoam-labs.org` is already registered in API
# Gateway (managed out of band, together with its ACM certificate and the
# public DNS record pointing at its regional target). This stack only needs
# to attach an API mapping so requests to that domain reach this HTTP API's
# $default stage.
resource "aws_apigatewayv2_api_mapping" "main" {
  api_id      = aws_apigatewayv2_api.main.id
  domain_name = var.api_domain_name
  stage       = aws_apigatewayv2_stage.default.id
}
