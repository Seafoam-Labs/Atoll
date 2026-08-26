# CloudFront distribution fronting the internal ALB through a VPC origin.
#
# The edge used to be an HTTP API Gateway, but API Gateway (HTTP *and* REST
# APIs) cannot proxy WebSocket upgrades: the ECS task accepted the
# `Upgrade: websocket` handshake while the gateway turned it into a 400
# instead of handing the upgraded connection back to the client. CloudFront
# proxies WebSocket connections transparently, so Blazor Server's SignalR
# circuit runs over a real WebSocket end-to-end (edge -> VPC origin ENI ->
# ALB -> ECS task), with SSE / long polling kept as the automatic fallback.

# The ISSUED ACM certificate for the custom domain. CloudFront requires the
# certificate to live in us-east-1, which matches this stack's region.
data "aws_acm_certificate" "api_domain" {
  domain   = var.api_domain_name
  statuses = ["ISSUED"]
}

# CloudFront's origin-facing edge IP ranges. The ALB security group (in
# main.tf) allows inbound HTTP only from this list, so the internal ALB is
# reachable exclusively through CloudFront.
data "aws_ec2_managed_prefix_list" "cloudfront" {
  name = "com.amazonaws.global.cloudfront.origin-facing"
}

# VPC origin: CloudFront reaches the internal ALB through a service-managed
# ENI in the VPC, so the ALB never needs a public face. The ALB must be
# Active before this can be created, and first deployment of a VPC origin can
# take up to ~15 minutes. us-east-1 supports VPC origins in every AZ except
# use1-az3, which the subnet filter in main.tf already excludes.
resource "aws_cloudfront_vpc_origin" "main" {
  vpc_origin_endpoint_config {
    name       = "${var.project_name}-vpc-origin"
    arn        = aws_lb.main.arn
    http_port  = 80
    https_port = 443

    # TLS terminates at the CloudFront edge; the hop to the ALB inside the
    # VPC is plain HTTP (same as the previous API Gateway VPC link setup).
    origin_protocol_policy = "http-only"

    origin_ssl_protocols {
      items    = ["TLSv1.2"]
      quantity = 1
    }
  }

  tags = {
    Name = "${var.project_name}-vpc-origin"
  }
}

resource "aws_cloudfront_distribution" "main" {
  enabled     = true
  comment     = "${var.project_name} — Blazor UI + API (WebSocket-capable)"
  aliases     = [var.api_domain_name]
  price_class = "PriceClass_100"

  # AWS WAF web ACL (see waf.tf): rate limiting and any future edge rules.
  # WAF inspects the HTTP request/upgrade handshake; established WebSocket
  # frames are not re-inspected.
  web_acl_id = aws_wafv2_web_acl.main.arn

  origin {
    domain_name = aws_lb.main.dns_name
    origin_id   = "${var.project_name}-alb"

    vpc_origin_config {
      vpc_origin_id = aws_cloudfront_vpc_origin.main.id

      # Upper bound for slow origin responses (git smart HTTP transfers,
      # SignalR SSE fallback) before CloudFront gives up waiting. WebSocket
      # connections are not subject to it once upgraded.
      origin_read_timeout = 60
    }
  }

  default_cache_behavior {
    allowed_methods        = ["DELETE", "GET", "HEAD", "OPTIONS", "PATCH", "POST", "PUT"]
    cached_methods         = ["GET", "HEAD"]
    target_origin_id       = "${var.project_name}-alb"
    viewer_protocol_policy = "redirect-to-https"

    # The app is fully dynamic (Blazor circuits, git smart HTTP): never
    # cache, and forward every header, cookie, and query string so SignalR
    # negotiation, WebSocket upgrades, and antiforgery cookies behave exactly
    # like a direct connection to the ALB.
    cache_policy_id          = "4135ea2d-6df8-44a3-9df3-4b5a84be39ad" # Managed-CachingDisabled
    origin_request_policy_id = "216adef6-5c7f-47e4-b989-5492eafa07d3" # Managed-AllViewer
  }

  restrictions {
    geo_restriction {
      restriction_type = "none"
    }
  }

  viewer_certificate {
    acm_certificate_arn      = data.aws_acm_certificate.api_domain.arn
    ssl_support_method       = "sni-only"
    minimum_protocol_version = "TLSv1.2_2021"
  }

  tags = {
    Name = "${var.project_name}-distribution"
  }
}
