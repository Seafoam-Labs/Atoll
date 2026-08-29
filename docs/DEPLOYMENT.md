# Deployment

Atoll deploys to AWS via GitHub Actions (`.github/workflows/deploy.yml`) and Terraform
(`terraform/`). The pipeline authenticates with **GitHub OIDC federation** — AWS trusts short-lived
tokens minted by GitHub for this repository, so **no AWS credentials are stored anywhere** (not in
GitHub, not in Terraform state, not locally). This is what makes it safe to run from a public repo.

## Layout

| Piece | Location | Purpose |
| --- | --- | --- |
| Bootstrap stack | `terraform/bootstrap/` | One-time: state bucket, lock table, OIDC provider, deploy role |
| Main stack | `terraform/` | ECR, ECS Fargate, API Gateway, DocumentDB, secret wiring |
| Pipeline | `.github/workflows/deploy.yml` | Build, plan on PRs, apply + deploy on push to `main` |

## One-time setup (new AWS account)

Run the bootstrap stack once from a machine that has credentials for the new account
(its state stays local — it only creates the plumbing CI needs):

```bash
cd terraform/bootstrap
terraform init
terraform apply
```

If the bucket name collides, change `state_bucket_name` **and** the `bucket` value in
`terraform/backend.tf` together. The lock table name must likewise match `dynamodb_table` there.

Then copy the `deploy_role_arn` output into GitHub:

- GitHub repo → **Settings → Secrets and variables → Actions → Variables → New repository variable**
- Name: `AWS_DEPLOY_ROLE_ARN`, value: the `deploy_role_arn` output

This is a plain variable, not a secret — the ARN is not sensitive, and the role cannot be assumed
by anyone except this repository's `main` branch and same-repo pull requests (see trust policy in
`terraform/bootstrap/main.tf`). Fork PRs never match the trust policy and get no AWS access.

After that, pushing to `main` deploys; opening a PR from a same-repo branch runs `terraform plan`.

## Adopting or recovering existing bootstrap resources

Because the bootstrap stack (`terraform/bootstrap/`) maintains local state, you must import the resources if you are
running Terraform from a fresh machine against an already bootstrapped account, or if resources like the S3 state
bucket, ECR repository, or GitHub OIDC provider already exist in the AWS account:

```bash
cd terraform/bootstrap
terraform init

# Resolve account ID and resource names (adjust if customized in variables.tf)
ACCOUNT_ID=$(aws sts get-caller-identity --query Account --output text)
BUCKET_NAME="seafoam-atoll-tfstate"
PROJECT_NAME="atoll-api"
LOCK_TABLE="atoll-api-terraform-locks"

# 1. State bucket and configurations
terraform import aws_s3_bucket.tfstate "${BUCKET_NAME}"
terraform import aws_s3_bucket_versioning.tfstate "${BUCKET_NAME}"
terraform import aws_s3_bucket_server_side_encryption_configuration.tfstate "${BUCKET_NAME}"
terraform import aws_s3_bucket_public_access_block.tfstate "${BUCKET_NAME}"

# 2. DynamoDB lock table
terraform import aws_dynamodb_table.tf_lock "${LOCK_TABLE}"

# 3. ECR Repository
terraform import aws_ecr_repository.app "${PROJECT_NAME}"

# 4. GitHub OIDC Provider (only one allowed per AWS account)
terraform import aws_iam_openid_connect_provider.github "arn:aws:iam::${ACCOUNT_ID}:oidc-provider/token.actions.githubusercontent.com"

# 5. IAM Role, Policy, and Attachment
terraform import aws_iam_role.github_deploy "${PROJECT_NAME}-github-deploy"
terraform import aws_iam_policy.github_deploy "arn:aws:iam::${ACCOUNT_ID}:policy/${PROJECT_NAME}-github-deploy"
terraform import aws_iam_role_policy_attachment.github_deploy "${PROJECT_NAME}-github-deploy/arn:aws:iam::${ACCOUNT_ID}:policy/${PROJECT_NAME}-github-deploy"
```

Once imported, run `terraform plan` to confirm that the local state matches the live infrastructure without unexpected
changes.

> **Note on the Main Stack (`terraform/`):** Unlike bootstrap, the main stack stores its state remotely in the S3 bucket
> (`terraform/backend.tf`). Once bootstrap is complete, running `terraform init` locally or via GitHub Actions
> automatically loads the state from S3—no manual imports are required.

## What the pipeline does

1. `build-image` — builds the container image on every PR (validates the Dockerfile, no AWS).
2. `plan` — same-repo PRs: `terraform fmt`, `init`, `plan`. The plan output is posted as a
   sticky comment on the PR (updated in place on each push, using the built-in `GITHUB_TOKEN`).
3. `deploy` — pushes to `main`: builds and pushes the image to ECR tagged with the commit SHA
   (tags are immutable), then `terraform apply -var image_tag=<sha>`. Updating the task definition
   makes ECS roll the service to the new image.

## Storage: Amazon DocumentDB

The production store is a DocumentDB cluster (`terraform/docdb.tf`):

- Instance class `db.t4g.medium` — the only T4G (Graviton2) class DocumentDB offers.
- TLS is enabled (DocumentDB default). The Amazon CA bundle is baked into the image at
  `/app/rds-combined-ca-bundle.pem` by `Atoll.Api/Dockerfile` and referenced via `tlsCAFile` in
  the connection string.
- Port 27017 is reachable only from the ECS service's security group.
- The master password is generated by Terraform (`random_password`) and never rendered: the full
  connection string is written to Secrets Manager (`atoll-api/mongo-connection-string`) and
  injected into the container as `Atoll__Mongo__ConnectionString` via the task definition's
  `secrets` block. It never appears in logs, the workflow, or environment listings.

DocumentDB is MongoDB wire-compatible but not MongoDB; if you use features it does not support
(change streams, some aggregation stages), the app code needs adjusting.

## Reverse proxies and forwarded headers

The app processes forwarded client IP and scheme values from trusted proxies. Configure the
`Atoll:Proxy` section directly or with environment variables:

| Env var | Purpose |
| --- | --- |
| `Atoll__Proxy__KnownNetworks` | Comma-separated trusted networks in CIDR notation |
| `Atoll__Proxy__KnownProxies` | Comma-separated trusted proxy IP addresses |
| `Atoll__Proxy__ForwardedProtoHeaderName` | Scheme header name; defaults to `X-Forwarded-Proto` |
| `Atoll__Proxy__ForwardLimit` | Maximum forwarded entries to process; defaults to `1` |

Only configure networks and proxies that can reach the application directly. Invalid CIDRs fail
at startup.

The current AWS deployment uses the VPC CIDR, a limit of `2`, and
`CloudFront-Forwarded-Proto`. Its CloudFront policy forwards that generated header to the origin.

## Transport security & response compression

Atoll enables in-app response compression (Brotli/Gzip) for dynamic HTML and API responses. ASP.NET Core disables
compression for HTTPS by default (`EnableForHttps = false`), which matches Atoll's standard deployment: TLS is
terminated at the proxy/ALB and the proxy-to-Kestrel hop is plain HTTP, so compression applies without exposing
CRIME/BREACH side channels. If TLS is ever terminated directly in Kestrel with `EnableForHttps = true`, pages
containing per-user secrets or tokens need BREACH mitigations (randomized padding, antiforgery token masking);
residual risk is low for the current public-metadata mirror.

## Decommissioning the old account

The move is a fresh deploy — nothing is imported. When the new deployment is verified, tear down
the old account's resources with whatever was managing them there.
