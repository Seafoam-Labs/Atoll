terraform {
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
}

provider "aws" {
  region = var.aws_region
}

data "aws_vpc" "default" {
  default = true
}

data "aws_subnets" "default" {
  filter {
    name   = "vpc-id"
    values = [data.aws_vpc.default.id]
  }

  filter {
    name   = "availability-zone-id"
    values = ["use1-az1", "use1-az2", "use1-az4", "use1-az5", "use1-az6"]
  }
}

resource "aws_security_group" "ecs_sg" {
  name        = "${var.project_name}-ecs-sg"
  description = "Allow inbound traffic for Atoll API on Fargate"
  vpc_id      = data.aws_vpc.default.id

  ingress {
    description     = "Allow ALB traffic to the API port"
    from_port       = var.container_port
    to_port         = var.container_port
    protocol        = "tcp"
    security_groups = [aws_security_group.alb_sg.id]
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = {
    Name = "${var.project_name}-ecs-sg"
  }
}

resource "aws_security_group" "alb_sg" {
  name_prefix = "${var.project_name}-alb-sg-"
  description = "Allow inbound HTTP traffic from CloudFront (VPC origin) to the ALB"
  vpc_id      = data.aws_vpc.default.id

  # Ensure ecs_sg's referencing rules are removed before alb_sg is destroyed
  lifecycle {
    create_before_destroy = true
  }

  # The ALB is internal and only reachable from the service-managed ENI that
  # CloudFront creates for the VPC origin (see cloudfront.tf). TLS termination
  # happens at the CloudFront edge, so the hop into the VPC is plain HTTP.
  ingress {
    description     = "HTTP from CloudFront"
    from_port       = 80
    to_port         = 80
    protocol        = "tcp"
    prefix_list_ids = [data.aws_ec2_managed_prefix_list.cloudfront.id]
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = {
    Name = "${var.project_name}-alb-sg"
  }
}

# The repository is created by the bootstrap stack (terraform/bootstrap): the
# pipeline pushes the first image before this stack applies, so the repository
# must already exist.
data "aws_ecr_repository" "app" {
  name = var.project_name
}

resource "aws_ecs_cluster" "main" {
  name = "${var.project_name}-cluster"
}

resource "aws_iam_role" "ecs_task_execution_role" {
  name = "${var.project_name}-execution-role"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Action = "sts:AssumeRole"
        Effect = "Allow"
        Principal = {
          Service = "ecs-tasks.amazonaws.com"
        }
      }
    ]
  })
}

resource "aws_iam_role_policy_attachment" "ecs_task_execution_role_policy" {
  role       = aws_iam_role.ecs_task_execution_role.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}


resource "aws_cloudwatch_log_group" "ecs_logs" {
  name              = "/ecs/${var.project_name}"
  retention_in_days = 7
}

resource "aws_ecs_task_definition" "app" {
  family                   = var.project_name
  network_mode             = "awsvpc"
  requires_compatibilities = ["FARGATE"]
  cpu                      = var.cpu
  memory                   = var.memory
  execution_role_arn       = aws_iam_role.ecs_task_execution_role.arn

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "ARM64"
  }

  container_definitions = jsonencode([
    {
      name      = var.project_name
      image     = "${data.aws_ecr_repository.app.repository_url}:${var.image_tag}"
      essential = true
      portMappings = [
        {
          containerPort = var.container_port
          hostPort      = var.container_port
        }
      ]
      environment = [
        {
          name  = "DataFile"
          value = "/app/data/packages-meta-ext-v1.json"
        },
        {
          name  = "ASPNETCORE_URLS"
          value = "http://+:8080"
        },
        {
          name  = "Atoll__Mutations__Enabled"
          value = "false"
        },
        {
          name  = "Atoll__Ui__ExternalBaseUrl"
          value = "https://${var.api_domain_name}"
        },
        {
          # Trust proxy hops within the VPC. The task security group only accepts ALB traffic.
          name  = "Atoll__Proxy__KnownNetworks"
          value = data.aws_vpc.default.cidr_block
        },
        {
          # Use the scheme recorded before CloudFront forwards over HTTP.
          name  = "Atoll__Proxy__ForwardedProtoHeaderName"
          value = "CloudFront-Forwarded-Proto"
        },
        {
          # Process both proxy hops to restore the client IP.
          name  = "Atoll__Proxy__ForwardLimit"
          value = "2"
        }
      ]
      secrets = [
        {
          name      = "Atoll__Mongo__ConnectionString"
          valueFrom = aws_secretsmanager_secret.mongo_connection_string.arn
        }
      ]
      logConfiguration = {
        logDriver = "awslogs"
        options = {
          "awslogs-group"         = aws_cloudwatch_log_group.ecs_logs.name
          "awslogs-region"        = var.aws_region
          "awslogs-stream-prefix" = "ecs"
        }
      }
      healthCheck = {
        command     = ["CMD-SHELL", "curl -fsS http://localhost:${var.container_port}/health || exit 1"]
        interval    = 30
        timeout     = 5
        retries     = 3
        startPeriod = 30
      }
    }
  ])
}

# Application Load Balancer. Sits behind CloudFront via a VPC origin (see
# cloudfront.tf) and transparently proxies the HTTP `Upgrade: websocket`
# handshake to the ECS tasks, so Blazor Server's SignalR circuit runs over a
# real WebSocket instead of falling back to long polling.
resource "aws_lb" "main" {
  name = "${var.project_name}-alb"
  # Fronted by CloudFront via a VPC origin (see cloudfront.tf); the ALB is
  # only reachable from CloudFront's origin-facing edge ranges.
  internal           = true
  load_balancer_type = "application"
  security_groups    = [aws_security_group.alb_sg.id]
  subnets            = data.aws_subnets.default.ids

  # Blazor keeps a long-lived, mostly-idle WebSocket open per circuit; a
  # generous idle timeout avoids tearing down live-but-quiet connections.
  idle_timeout = 300

  tags = {
    Name = "${var.project_name}-alb"
  }
}

resource "aws_lb_target_group" "main" {
  name        = "${var.project_name}-tg"
  port        = var.container_port
  protocol    = "HTTP"
  vpc_id      = data.aws_vpc.default.id
  target_type = "ip" # Fargate awsvpc tasks register by IP, not instance

  health_check {
    path                = "/health"
    matcher             = "200"
    interval            = 30
    timeout             = 5
    healthy_threshold   = 2
    unhealthy_threshold = 3
  }

  # Pin each client to the task that owns its Blazor circuit so the WebSocket
  # and its preceding negotiate request land on the same instance once the
  # service scales beyond one task.
  stickiness {
    type            = "lb_cookie"
    enabled         = false # Not needed for single instance
    cookie_duration = 86400
  }
}

resource "aws_lb_listener" "http" {
  load_balancer_arn = aws_lb.main.arn
  port              = 80
  protocol          = "HTTP"

  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.main.arn
  }
}

resource "aws_ecs_service" "main" {
  name            = "${var.project_name}-service"
  cluster         = aws_ecs_cluster.main.id
  task_definition = aws_ecs_task_definition.app.arn
  desired_count   = 1
  launch_type     = "FARGATE"

  network_configuration {
    subnets          = data.aws_subnets.default.ids
    security_groups  = [aws_security_group.ecs_sg.id]
    assign_public_ip = true
  }

  load_balancer {
    target_group_arn = aws_lb_target_group.main.arn
    container_name   = var.project_name
    container_port   = var.container_port
  }

  # Let the app finish booting (seed/index warm-up) before the ALB starts
  # counting failed health checks against it.
  health_check_grace_period_seconds = 120

  depends_on = [aws_lb_listener.http]
}
