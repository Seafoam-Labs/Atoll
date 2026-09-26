# CloudWatch dashboard over the metrics AWS already publishes for the resources
# in this stack: no agent in the task, no extra cost beyond standard metrics.
#
# The application's own atoll_* series (see observability/grafana/atoll.json)
# are deliberately absent — nothing in AWS scrapes /metrics or receives OTLP
# from the Fargate task yet, so there is no collector for them to land in.

locals {
  # ALB, ECS, and DocumentDB publish every minute. CloudFront and WAF publish
  # every five minutes, so edge panels — and panels that mix edge with origin
  # series — use the coarser period rather than rendering as gaps.
  period_origin = 60
  period_edge   = 300
}

resource "aws_cloudwatch_dashboard" "main" {
  dashboard_name = "${var.project_name}-overview"

  dashboard_body = jsonencode({
    start = "-PT24H"

    widgets = [
      {
        type   = "metric"
        x      = 0
        y      = 0
        width  = 8
        height = 6
        properties = {
          title  = "Requests"
          region = var.aws_region
          view   = "timeSeries"
          stat   = "Sum"
          period = local.period_edge
          metrics = [
            [
              "AWS/CloudFront", "Requests",
              "DistributionId", aws_cloudfront_distribution.main.id,
              "Region", "Global",
              { label = "CloudFront" },
            ],
            [
              "AWS/ApplicationELB", "RequestCount",
              "LoadBalancer", aws_lb.main.arn_suffix,
              "TargetGroup", aws_lb_target_group.main.arn_suffix,
              { label = "ALB" },
            ],
          ]
        }
      },
      {
        type   = "metric"
        x      = 8
        y      = 0
        width  = 8
        height = 6
        properties = {
          title  = "Edge error rate"
          region = var.aws_region
          view   = "timeSeries"
          stat   = "Average"
          period = local.period_edge
          metrics = [
            [
              "AWS/CloudFront", "4xxErrorRate",
              "DistributionId", aws_cloudfront_distribution.main.id,
              "Region", "Global",
              { label = "4xx %" },
            ],
            [
              "AWS/CloudFront", "5xxErrorRate",
              "DistributionId", aws_cloudfront_distribution.main.id,
              "Region", "Global",
              { label = "5xx %" },
            ],
          ]
        }
      },
      {
        type   = "metric"
        x      = 16
        y      = 0
        width  = 8
        height = 6
        properties = {
          title  = "WAF rate-limit rule"
          region = var.aws_region
          view   = "timeSeries"
          stat   = "Sum"
          period = local.period_edge
          metrics = [
            # WebACL and Rule take the visibility_config metric names from
            # waf.tf, not the resource names; keep the two files in step.
            [
              "AWS/WAFv2", "AllowedRequests",
              "Region", "Global",
              "WebACL", "${var.project_name}-waf",
              "Rule", "${var.project_name}-rate-limit",
              { label = "allowed" },
            ],
            [
              "AWS/WAFv2", "BlockedRequests",
              "Region", "Global",
              "WebACL", "${var.project_name}-waf",
              "Rule", "${var.project_name}-rate-limit",
              { label = "blocked" },
            ],
          ]
        }
      },
      {
        type   = "metric"
        x      = 0
        y      = 6
        width  = 8
        height = 6
        properties = {
          title  = "Origin response time"
          region = var.aws_region
          view   = "timeSeries"
          stat   = "Average"
          period = local.period_edge
          metrics = [
            [
              "AWS/ApplicationELB", "TargetResponseTime",
              "LoadBalancer", aws_lb.main.arn_suffix,
              "TargetGroup", aws_lb_target_group.main.arn_suffix,
              { label = "ALB target avg" },
            ],
            [
              "AWS/ApplicationELB", "TargetResponseTime",
              "LoadBalancer", aws_lb.main.arn_suffix,
              "TargetGroup", aws_lb_target_group.main.arn_suffix,
              { label = "ALB target p95", stat = "p95" },
            ],
            [
              "AWS/CloudFront", "OriginLatency",
              "DistributionId", aws_cloudfront_distribution.main.id,
              "Region", "Global",
              { label = "CloudFront origin avg" },
            ],
          ]
        }
      },
      {
        type   = "metric"
        x      = 8
        y      = 6
        width  = 8
        height = 6
        properties = {
          title   = "ALB target 4xx / 5xx"
          region  = var.aws_region
          view    = "timeSeries"
          stacked = true
          stat    = "Sum"
          period  = local.period_origin
          metrics = [
            [
              "AWS/ApplicationELB", "HTTPCode_Target_4XX_Count",
              "LoadBalancer", aws_lb.main.arn_suffix,
              "TargetGroup", aws_lb_target_group.main.arn_suffix,
              { label = "4xx" },
            ],
            [
              "AWS/ApplicationELB", "HTTPCode_Target_5XX_Count",
              "LoadBalancer", aws_lb.main.arn_suffix,
              "TargetGroup", aws_lb_target_group.main.arn_suffix,
              { label = "5xx" },
            ],
          ]
        }
      },
      {
        type   = "metric"
        x      = 16
        y      = 6
        width  = 8
        height = 6
        properties = {
          title  = "ALB target health"
          region = var.aws_region
          view   = "timeSeries"
          stat   = "Maximum"
          period = local.period_origin
          metrics = [
            [
              "AWS/ApplicationELB", "HealthyHostCount",
              "LoadBalancer", aws_lb.main.arn_suffix,
              "TargetGroup", aws_lb_target_group.main.arn_suffix,
              { label = "healthy" },
            ],
            [
              "AWS/ApplicationELB", "UnhealthyHostCount",
              "LoadBalancer", aws_lb.main.arn_suffix,
              "TargetGroup", aws_lb_target_group.main.arn_suffix,
              { label = "unhealthy" },
            ],
          ]
        }
      },
      {
        type   = "metric"
        x      = 0
        y      = 12
        width  = 8
        height = 6
        properties = {
          title  = "ECS CPU utilization"
          region = var.aws_region
          view   = "timeSeries"
          stat   = "Average"
          period = local.period_origin
          metrics = [
            [
              "AWS/ECS", "CPUUtilization",
              "ClusterName", aws_ecs_cluster.main.name,
              "ServiceName", aws_ecs_service.main.name,
              { label = "avg" },
            ],
            [
              "AWS/ECS", "CPUUtilization",
              "ClusterName", aws_ecs_cluster.main.name,
              "ServiceName", aws_ecs_service.main.name,
              { label = "max", stat = "Maximum" },
            ],
          ]
        }
      },
      {
        type   = "metric"
        x      = 8
        y      = 12
        width  = 8
        height = 6
        properties = {
          title  = "ECS memory utilization"
          region = var.aws_region
          view   = "timeSeries"
          stat   = "Average"
          period = local.period_origin
          metrics = [
            [
              "AWS/ECS", "MemoryUtilization",
              "ClusterName", aws_ecs_cluster.main.name,
              "ServiceName", aws_ecs_service.main.name,
              { label = "avg" },
            ],
            [
              "AWS/ECS", "MemoryUtilization",
              "ClusterName", aws_ecs_cluster.main.name,
              "ServiceName", aws_ecs_service.main.name,
              { label = "max", stat = "Maximum" },
            ],
          ]
        }
      },
      {
        type   = "metric"
        x      = 16
        y      = 12
        width  = 8
        height = 6
        properties = {
          title  = "ECS tasks"
          region = var.aws_region
          view   = "timeSeries"
          stat   = "Maximum"
          period = local.period_origin
          metrics = [
            [
              "AWS/ECS", "RunningTaskCount",
              "ClusterName", aws_ecs_cluster.main.name,
              "ServiceName", aws_ecs_service.main.name,
              { label = "running" },
            ],
            [
              "AWS/ECS", "PendingTaskCount",
              "ClusterName", aws_ecs_cluster.main.name,
              "ServiceName", aws_ecs_service.main.name,
              { label = "pending" },
            ],
          ]
        }
      },
      {
        type   = "metric"
        x      = 0
        y      = 18
        width  = 8
        height = 6
        properties = {
          title  = "DocumentDB CPU utilization"
          region = var.aws_region
          view   = "timeSeries"
          stat   = "Average"
          period = local.period_origin
          metrics = [
            [
              "AWS/DocDB", "CPUUtilization",
              "DBInstanceIdentifier", aws_docdb_cluster_instance.main.id,
              { label = "avg" },
            ],
            [
              "AWS/DocDB", "CPUUtilization",
              "DBInstanceIdentifier", aws_docdb_cluster_instance.main.id,
              { label = "max", stat = "Maximum" },
            ],
          ]
        }
      },
      {
        type   = "metric"
        x      = 8
        y      = 18
        width  = 8
        height = 6
        properties = {
          title  = "DocumentDB connections / freeable memory"
          region = var.aws_region
          view   = "timeSeries"
          stat   = "Maximum"
          period = local.period_origin
          metrics = [
            [
              "AWS/DocDB", "DatabaseConnections",
              "DBInstanceIdentifier", aws_docdb_cluster_instance.main.id,
              { label = "connections" },
            ],
            # Different unit (bytes vs count), so it gets its own axis.
            [
              "AWS/DocDB", "FreeableMemory",
              "DBInstanceIdentifier", aws_docdb_cluster_instance.main.id,
              { label = "freeable memory", stat = "Minimum", yAxis = "right" },
            ],
          ]
        }
      },
      {
        type   = "metric"
        x      = 16
        y      = 18
        width  = 8
        height = 6
        properties = {
          title  = "DocumentDB volume IOPS"
          region = var.aws_region
          view   = "timeSeries"
          stat   = "Average"
          period = local.period_origin
          metrics = [
            [
              "AWS/DocDB", "VolumeReadIOPs",
              "DBClusterIdentifier", aws_docdb_cluster.main.id,
              { label = "read" },
            ],
            [
              "AWS/DocDB", "VolumeWriteIOPs",
              "DBClusterIdentifier", aws_docdb_cluster.main.id,
              { label = "write" },
            ],
          ]
        }
      },
    ]
  })
}
