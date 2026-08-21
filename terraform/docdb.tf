resource "random_password" "docdb_master" {
  length  = 24
  special = false
}

resource "aws_docdb_subnet_group" "main" {
  name       = "${var.project_name}-docdb"
  subnet_ids = data.aws_subnets.default.ids

  tags = {
    Name = "${var.project_name}-docdb-subnets"
  }
}

resource "aws_security_group" "docdb_sg" {
  name        = "${var.project_name}-docdb-sg"
  description = "Allow DocumentDB traffic from the Atoll ECS service only"
  vpc_id      = data.aws_vpc.default.id

  ingress {
    description     = "DocumentDB from ECS tasks"
    from_port       = 27017
    to_port         = 27017
    protocol        = "tcp"
    security_groups = [aws_security_group.ecs_sg.id]
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = {
    Name = "${var.project_name}-docdb-sg"
  }
}

resource "aws_docdb_cluster" "main" {
  cluster_identifier      = "${var.project_name}-docdb"
  engine                  = "docdb"
  engine_version          = var.docdb_engine_version
  master_username         = var.docdb_master_username
  master_password         = random_password.docdb_master.result
  db_subnet_group_name    = aws_docdb_subnet_group.main.name
  vpc_security_group_ids  = [aws_security_group.docdb_sg.id]
  storage_encrypted       = true
  backup_retention_period = 7
  preferred_backup_window = "07:00-09:00"
  skip_final_snapshot     = true
}

resource "aws_docdb_cluster_instance" "main" {
  identifier         = "${var.project_name}-docdb-1"
  cluster_identifier = aws_docdb_cluster.main.id
  engine             = "docdb"
  instance_class     = var.docdb_instance_class
}

locals {
  # TLS is enabled on the cluster by default; the CA bundle path matches the
  # file baked into the container image (see Atoll.Api/Dockerfile).
  docdb_connection_string = join("", [
    "mongodb://${var.docdb_master_username}:${random_password.docdb_master.result}",
    "@${aws_docdb_cluster.main.endpoint}:27017/${var.mongo_database}",
    "?tls=true&tlsCAFile=/app/rds-combined-ca-bundle.pem",
    "&replicaSet=rs0&readPreference=secondaryPreferred",
    "&retryWrites=false&authSource=admin",
  ])
}

resource "aws_secretsmanager_secret" "mongo_connection_string" {
  name        = "${var.project_name}/mongo-connection-string"
  description = "MongoDB-compatible connection string for the Atoll API (Amazon DocumentDB)"
}

resource "aws_secretsmanager_secret_version" "mongo_connection_string" {
  secret_id     = aws_secretsmanager_secret.mongo_connection_string.id
  secret_string = local.docdb_connection_string
}

resource "aws_iam_role_policy" "ecs_task_execution_secrets" {
  name = "${var.project_name}-execution-secrets"
  role = aws_iam_role.ecs_task_execution_role.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect = "Allow"
        Action = ["secretsmanager:GetSecretValue"]
        Resource = [
          aws_secretsmanager_secret.mongo_connection_string.arn
        ]
      }
    ]
  })
}
