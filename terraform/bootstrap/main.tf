terraform {
  required_version = ">= 1.5.0"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.0"
    }
    tls = {
      source  = "hashicorp/tls"
      version = "~> 4.0"
    }
  }
}

provider "aws" {
  region = var.aws_region
}

data "aws_caller_identity" "current" {}

locals {
  account_id  = data.aws_caller_identity.current.account_id
  oidc_issuer = replace(aws_iam_openid_connect_provider.github.url, "https://", "")

  # Subjects allowed to assume the deploy role. Pushes to the deploy branch run
  # deploys; pull_request covers terraform plan on same-repo PRs. Fork PRs run
  # under the fork's subject and never match, so they get no AWS access.
  deploy_subjects = [
    "repo:${var.github_repo}:ref:refs/heads/${var.deploy_branch}",
    "repo:${var.github_repo}:pull_request",
  ]
}

# --- Terraform remote state backend ---

resource "aws_s3_bucket" "tfstate" {
  bucket = var.state_bucket_name
}

resource "aws_s3_bucket_versioning" "tfstate" {
  bucket = aws_s3_bucket.tfstate.id

  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "tfstate" {
  bucket = aws_s3_bucket.tfstate.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
    bucket_key_enabled = true
  }
}

resource "aws_s3_bucket_public_access_block" "tfstate" {
  bucket = aws_s3_bucket.tfstate.id

  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_dynamodb_table" "tf_lock" {
  name         = var.lock_table_name
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "LockID"

  attribute {
    name = "LockID"
    type = "S"
  }
}

# --- Container image repository ---
# Lives here rather than in the main stack: the pipeline pushes the first image
# before the main stack applies, so the repository must already exist.

resource "aws_ecr_repository" "app" {
  name                 = var.project_name
  image_tag_mutability = "IMMUTABLE"
  force_delete         = true

  image_scanning_configuration {
    scan_on_push = true
  }
}

# --- GitHub OIDC federation (no static AWS credentials anywhere) ---

data "tls_certificate" "github" {
  url = "https://token.actions.githubusercontent.com"
}

resource "aws_iam_openid_connect_provider" "github" {
  url             = data.tls_certificate.github.url
  client_id_list  = ["sts.amazonaws.com"]
  thumbprint_list = [data.tls_certificate.github.certificates[0].sha1_fingerprint]
}

resource "aws_iam_role" "github_deploy" {
  name = "${var.project_name}-github-deploy"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect = "Allow"
        Principal = {
          Federated = aws_iam_openid_connect_provider.github.arn
        }
        Action = "sts:AssumeRoleWithWebIdentity"
        Condition = {
          StringEquals = {
            "${local.oidc_issuer}:aud" = "sts.amazonaws.com"
          }
          StringLike = {
            "${local.oidc_issuer}:sub" = local.deploy_subjects
          }
        }
      }
    ]
  })
}

resource "aws_iam_policy" "github_deploy" {
  name        = "${var.project_name}-github-deploy"
  description = "Permissions GitHub Actions needs to run the Atoll terraform stacks"

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid    = "TerraformStateBucket"
        Effect = "Allow"
        Action = ["s3:ListBucket", "s3:GetBucketVersioning"]
        Resource = [
          aws_s3_bucket.tfstate.arn
        ]
      },
      {
        Sid    = "TerraformStateObjects"
        Effect = "Allow"
        Action = ["s3:GetObject", "s3:PutObject", "s3:DeleteObject"]
        Resource = [
          "${aws_s3_bucket.tfstate.arn}/*"
        ]
      },
      {
        Sid    = "TerraformLocks"
        Effect = "Allow"
        Action = ["dynamodb:PutItem", "dynamodb:GetItem", "dynamodb:DeleteItem", "dynamodb:DescribeTable"]
        Resource = [
          aws_dynamodb_table.tf_lock.arn
        ]
      },
      {
        Sid      = "CallerIdentity"
        Effect   = "Allow"
        Action   = ["sts:GetCallerIdentity"]
        Resource = "*"
      },
      {
        Sid    = "ReadVpcTopology"
        Effect = "Allow"
        Action = [
          "ec2:DescribeVpcs",
          "ec2:DescribeSubnets",
          "ec2:DescribeAvailabilityZones",
          "ec2:DescribeSecurityGroups",
          "ec2:DescribeNetworkInterfaces",
          "ec2:DescribeVpcAttribute",
        ]
        Resource = "*"
      },
      {
        Sid    = "ManageSecurityGroups"
        Effect = "Allow"
        Action = [
          "ec2:CreateSecurityGroup",
          "ec2:DeleteSecurityGroup",
          "ec2:AuthorizeSecurityGroupIngress",
          "ec2:AuthorizeSecurityGroupEgress",
          "ec2:RevokeSecurityGroupIngress",
          "ec2:RevokeSecurityGroupEgress",
          "ec2:CreateTags",
          "ec2:DeleteTags",
        ]
        Resource = "*"
      },
      {
        Sid    = "Ecr"
        Effect = "Allow"
        Action = [
          "ecr:GetAuthorizationToken",
          "ecr:CreateRepository",
          "ecr:DeleteRepository",
          "ecr:DescribeRepositories",
          "ecr:DescribeImages",
          "ecr:DescribeImageScanFindings",
          "ecr:ListTagsForResource",
          "ecr:TagResource",
          "ecr:UntagResource",
          "ecr:BatchCheckLayerAvailability",
          "ecr:BatchGetImage",
          "ecr:GetDownloadUrlForLayer",
          "ecr:InitiateLayerUpload",
          "ecr:UploadLayerPart",
          "ecr:CompleteLayerUpload",
          "ecr:PutImage",
        ]
        Resource = "*"
      },
      {
        Sid      = "Ecs"
        Effect   = "Allow"
        Action   = ["ecs:*"]
        Resource = "*"
      },
      {
        Sid      = "ServiceDiscovery"
        Effect   = "Allow"
        Action   = ["servicediscovery:*"]
        Resource = "*"
      },
      {
        # A private DNS namespace is backed by a Route 53 private hosted zone,
        # which Cloud Map creates/deletes using the caller's permissions.
        Sid    = "ServiceDiscoveryDns"
        Effect = "Allow"
        Action = [
          "route53:CreateHostedZone",
          "route53:DeleteHostedZone",
        ]
        Resource = "*"
      },
      {
        Sid    = "ApiGateway"
        Effect = "Allow"
        Action = ["apigateway:*"]
        Resource = [
          "arn:aws:apigateway:${var.aws_region}::/*"
        ]
      },
      {
        Sid    = "DocumentDb"
        Effect = "Allow"
        Action = [
          "rds:CreateDBCluster",
          "rds:DeleteDBCluster",
          "rds:DescribeDBClusters",
          "rds:ModifyDBCluster",
          "rds:CreateDBInstance",
          "rds:DeleteDBInstance",
          "rds:DescribeDBInstances",
          "rds:ModifyDBInstance",
          "rds:RebootDBInstance",
          "rds:CreateDBSubnetGroup",
          "rds:DeleteDBSubnetGroup",
          "rds:DescribeDBSubnetGroups",
          "rds:ModifyDBSubnetGroup",
          "rds:DescribeOrderableDBInstanceOptions",
          "rds:DescribeDBClusterParameters",
          "rds:AddTagsToResource",
          "rds:RemoveTagsFromResource",
          "rds:ListTagsForResource",
        ]
        Resource = "*"
      },
      {
        Sid    = "AppSecrets"
        Effect = "Allow"
        Action = [
          "secretsmanager:CreateSecret",
          "secretsmanager:DeleteSecret",
          "secretsmanager:DescribeSecret",
          "secretsmanager:GetResourcePolicy",
          "secretsmanager:GetSecretValue",
          "secretsmanager:PutSecretValue",
          "secretsmanager:UpdateSecret",
          "secretsmanager:RestoreSecret",
          "secretsmanager:TagResource",
          "secretsmanager:UntagResource",
        ]
        Resource = [
          "arn:aws:secretsmanager:${var.aws_region}:${local.account_id}:secret:${var.project_name}/*"
        ]
      },
      {
        Sid    = "CloudWatchLogs"
        Effect = "Allow"
        Action = [
          "logs:CreateLogGroup",
          "logs:DeleteLogGroup",
          "logs:PutRetentionPolicy",
          "logs:TagLogGroup",
          "logs:UntagLogGroup",
          "logs:ListTagsLogGroup",
          "logs:ListTagsForResource",
        ]
        Resource = [
          "arn:aws:logs:${var.aws_region}:${local.account_id}:log-group:/ecs/${var.project_name}*"
        ]
      },
      {
        Sid    = "ReadLogGroups"
        Effect = "Allow"
        Action = ["logs:DescribeLogGroups"]
        Resource = [
          "arn:aws:logs:${var.aws_region}:${local.account_id}:log-group::log-stream:*"
        ]
      },
      {
        Sid    = "ManageAppIamRoles"
        Effect = "Allow"
        Action = [
          "iam:CreateRole",
          "iam:DeleteRole",
          "iam:GetRole",
          "iam:UpdateRole",
          "iam:TagRole",
          "iam:UntagRole",
          "iam:AttachRolePolicy",
          "iam:DetachRolePolicy",
          "iam:ListAttachedRolePolicies",
          "iam:PutRolePolicy",
          "iam:GetRolePolicy",
          "iam:DeleteRolePolicy",
          "iam:ListRolePolicies",
        ]
        Resource = [
          "arn:aws:iam::${local.account_id}:role/${var.project_name}-*"
        ]
      },
      {
        Sid    = "PassAppRolesToEcs"
        Effect = "Allow"
        Action = ["iam:PassRole"]
        Resource = [
          "arn:aws:iam::${local.account_id}:role/${var.project_name}-*"
        ]
        Condition = {
          StringEquals = {
            "iam:PassedToService" = "ecs-tasks.amazonaws.com"
          }
        }
      },
      {
        Sid    = "ServiceLinkedRoles"
        Effect = "Allow"
        Action = ["iam:CreateServiceLinkedRole"]
        Resource = [
          # DocumentDB shares the RDS control plane; creating a cluster needs
          # the RDS service-linked role. IAM has no docdb.amazonaws.com SLR
          # template, so that principal cannot be used here.
          "arn:aws:iam::${local.account_id}:role/aws-service-role/rds.amazonaws.com/AWSServiceRoleForRDS",
          # HTTP API VPC links are implemented with an NLB managed by API
          # Gateway, which needs the ELB service-linked role.
          "arn:aws:iam::${local.account_id}:role/aws-service-role/elasticloadbalancing.amazonaws.com/AWSServiceRoleForElasticLoadBalancing",
          # ECS uses this role to register/deregister tasks in Cloud Map
          # (the ECS service below uses service_registries).
          "arn:aws:iam::${local.account_id}:role/aws-service-role/ecs.amazonaws.com/AWSServiceRoleForECS",
        ]
        Condition = {
          StringEquals = {
            "iam:AWSServiceName" = [
              "rds.amazonaws.com",
              "elasticloadbalancing.amazonaws.com",
              "ecs.amazonaws.com",
            ]
          }
        }
      },
    ]
  })
}

resource "aws_iam_role_policy_attachment" "github_deploy" {
  role       = aws_iam_role.github_deploy.name
  policy_arn = aws_iam_policy.github_deploy.arn
}
