# Alarms for the deployed service. Delivery lives in the Buoy stack
# (Seafoam-Labs/buoy): every alarm action points at its shared SNS topic, whose
# Lambda reformats the notification for Fluxer.
#
# Two conventions matter here:
#
#   1. Alarm names are `<project>-<subject>` and descriptions name the subsystem,
#      because Buoy routes role mentions by matching a regex against the alarm
#      name, description, metric namespace, and metric name. Changing this text
#      can silently change who gets pinged.
#   2. Every alarm sends ok_actions too, so the channel shows the recovery. The
#      notifier does not ping roles on OK.
#
# Thresholds are first guesses. Revisit them against a week of the CloudWatch
# dashboard (monitoring.tf) rather than tuning blind.

locals {
  alarms_enabled = var.alerting_topic_arn != ""
}

# The service is down: nothing is running to serve traffic. Two minutes rather
# than one, so a task replacement that briefly dips does not page.
resource "aws_cloudwatch_metric_alarm" "tasks_running" {
  count = local.alarms_enabled ? 1 : 0

  alarm_name          = "${var.project_name}-tasks-running"
  alarm_description   = "No ECS tasks running for the atoll-api service; the service is down"
  namespace           = "AWS/ECS"
  metric_name         = "RunningTaskCount"
  statistic           = "Minimum"
  period              = 60
  evaluation_periods  = 2
  threshold           = 1
  comparison_operator = "LessThanThreshold"
  treat_missing_data  = "notBreaching"

  dimensions = {
    ClusterName = aws_ecs_cluster.main.name
    ServiceName = aws_ecs_service.main.name
  }

  alarm_actions = [var.alerting_topic_arn]
  ok_actions    = [var.alerting_topic_arn]

  tags = {
    Name = "${var.project_name}-tasks-running"
  }
}

# Tasks exist but are failing health checks, so the ALB has nowhere to route.
resource "aws_cloudwatch_metric_alarm" "targets_unhealthy" {
  count = local.alarms_enabled ? 1 : 0

  alarm_name          = "${var.project_name}-targets-unhealthy"
  alarm_description   = "ALB target group has unhealthy targets; the atoll-api task is failing health checks"
  namespace           = "AWS/ApplicationELB"
  metric_name         = "UnhealthyHostCount"
  statistic           = "Maximum"
  period              = 60
  evaluation_periods  = 2
  threshold           = 0
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"

  dimensions = {
    LoadBalancer = aws_lb.main.arn_suffix
    TargetGroup  = aws_lb_target_group.main.arn_suffix
  }

  alarm_actions = [var.alerting_topic_arn]
  ok_actions    = [var.alerting_topic_arn]

  tags = {
    Name = "${var.project_name}-targets-unhealthy"
  }
}

# Requests are reaching the task and the app is erroring. Five in five minutes
# keeps a single bad deploy blip from paging on a low-traffic service.
resource "aws_cloudwatch_metric_alarm" "origin_5xx" {
  count = local.alarms_enabled ? 1 : 0

  alarm_name          = "${var.project_name}-origin-5xx"
  alarm_description   = "The atoll-api origin is returning 5xx responses"
  namespace           = "AWS/ApplicationELB"
  metric_name         = "HTTPCode_Target_5XX_Count"
  statistic           = "Sum"
  period              = 300
  evaluation_periods  = 1
  threshold           = 5
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"

  dimensions = {
    LoadBalancer = aws_lb.main.arn_suffix
    TargetGroup  = aws_lb_target_group.main.arn_suffix
  }

  alarm_actions = [var.alerting_topic_arn]
  ok_actions    = [var.alerting_topic_arn]

  tags = {
    Name = "${var.project_name}-origin-5xx"
  }
}

# Streak alerting wants atoll_refresh_consecutive_failures, but no collector
# ships app metrics to CloudWatch (see monitoring.tf), so alarm on the warning
# the updater logs once per failed cycle instead. With the deployed 5-minute
# cycle, three events inside 15 minutes are necessarily consecutive: an
# interleaved success spreads four cycles past the window. Keep the pattern in
# sync with the LogWarning in PackageIndexUpdater and the window with
# Atoll__DataSource__RefreshIntervalMinutes.
resource "aws_cloudwatch_log_metric_filter" "metadata_refresh_failures" {
  count = local.alarms_enabled ? 1 : 0

  name           = "${var.project_name}-metadata-refresh-failures"
  log_group_name = aws_cloudwatch_log_group.ecs_logs.name
  pattern        = "\"Unable to fetch and store new package data.\""

  metric_transformation {
    name      = "MetadataRefreshFailures"
    namespace = "Atoll"
    value     = "1"
  }
}

resource "aws_cloudwatch_metric_alarm" "metadata_refresh_failures" {
  count = local.alarms_enabled ? 1 : 0

  alarm_name          = "${var.project_name}-metadata-refresh-failures"
  alarm_description   = "Three consecutive AUR metadata refresh cycles failed; the search index is going stale"
  namespace           = "Atoll"
  metric_name         = "MetadataRefreshFailures"
  statistic           = "Sum"
  period              = 900
  evaluation_periods  = 1
  threshold           = 3
  comparison_operator = "GreaterThanOrEqualToThreshold"
  treat_missing_data  = "notBreaching"

  alarm_actions = [var.alerting_topic_arn]
  ok_actions    = [var.alerting_topic_arn]

  tags = {
    Name = "${var.project_name}-metadata-refresh-failures"
  }
}

# DocumentDB saturation. db.t4g.medium is a small instance, so sustained high CPU
# usually means the refresh or seeding workload outgrew it. Fifteen minutes
# avoids paging on the startup index build.
resource "aws_cloudwatch_metric_alarm" "docdb_cpu" {
  count = local.alarms_enabled ? 1 : 0

  alarm_name          = "${var.project_name}-docdb-cpu"
  alarm_description   = "The DocumentDB database instance is CPU-saturated"
  namespace           = "AWS/DocDB"
  metric_name         = "CPUUtilization"
  statistic           = "Average"
  period              = 300
  evaluation_periods  = 3
  threshold           = 90
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"

  dimensions = {
    DBInstanceIdentifier = aws_docdb_cluster_instance.main.id
  }

  alarm_actions = [var.alerting_topic_arn]
  ok_actions    = [var.alerting_topic_arn]

  tags = {
    Name = "${var.project_name}-docdb-cpu"
  }
}
