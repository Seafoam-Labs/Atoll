# AWS WAF in front of the CloudFront distribution.
#
# The ALB only accepts traffic from CloudFront, so the edge is the single
# enforcement point: nothing can reach the app without passing through this
# web ACL. Scope must be CLOUDFRONT (and the provider region us-east-1) for
# the ACL to attach to a distribution.

resource "aws_wafv2_web_acl" "main" {
  name  = "${var.project_name}-waf"
  scope = "CLOUDFRONT"

  default_action {
    allow {}
  }

  # Per-viewer-IP rate limit. The CloudFront scope means WAF aggregates the
  # real viewer IP directly (no X-Forwarded-For inspection to configure).
  # The evaluation window defaults to 300s (60/120/300/600 are selectable via
  # `evaluation_window_sec`, left at the default here); offenders are blocked
  # until their rolling request count drops back under the limit. The minimum
  # value WAF accepts is 10. Tune via the `waf_rate_limit` variable — keep
  # git clone/fetch bursts (many POSTs to the smart-HTTP endpoints) in mind.
  # Editing the rule resets its counters, so an apply clears in-flight blocks
  # (rate limiting may pause up to a minute after the change).
  rule {
    name     = "rate-limit"
    priority = 1

    action {
      block {}
    }

    statement {
      rate_based_statement {
        limit              = var.waf_rate_limit
        aggregate_key_type = "IP"
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "${var.project_name}-rate-limit"
      sampled_requests_enabled   = true
    }
  }

  visibility_config {
    cloudwatch_metrics_enabled = true
    metric_name                = "${var.project_name}-waf"
    sampled_requests_enabled   = true
  }

  tags = {
    Name = "${var.project_name}-waf"
  }
}
