# Slice 9 — Infrastructure as Code Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An apply-ready Terraform configuration under `infra/` that stands up MarketPulse's entire runtime surface (VPC, ALB, ECS Fargate ×2, ECR ×2, RDS SQL Server Express, Amazon MQ for RabbitMQ, S3 + CloudFront) from an empty AWS account, plus the cost model, ADR-011, and CI validation.

**Architecture:** Two Terraform roots — `infra/bootstrap/` (local state; creates the S3 state bucket) and `infra/` (S3 backend with native lockfile; everything else). One child module, `modules/ecs-service`, instantiated twice. ECS tasks in public subnets (no NAT — ADR-011); RDS and MQ in private subnets. One CloudFront distribution fronts both the SPA (S3/OAC) and the API (`/api/*`, `/hubs/*` → ALB). No pipeline concerns: image pushes, SPA upload, blue-green, and Lambda are slice 10.

**Tech Stack:** Terraform ≥ 1.10, AWS provider `~> 6.0`, `random` provider `~> 3.7`, Docker multi-stage builds, GitHub Actions.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-08-07-infrastructure-as-code-design.md`. Deviations get recorded there, not silently made.
- `required_version = ">= 1.10"` in both roots (S3 native locking needs it; local Terraform is v1.15.7).
- Region default `ap-southeast-2`; provider `default_tags` `Project = "MarketPulse"`, `ManagedBy = "Terraform"` in both roots.
- No NAT gateway, no VPC endpoints, no DynamoDB lock table, no multi-env layout — these are decided in the spec; do not add them.
- After every task: `terraform fmt -recursive` then `terraform validate` must pass on every root the task touched (`terraform init -backend=false` first when `.terraform` is missing). No AWS credentials exist in this session — never run `terraform plan` or `apply`.
- Commit messages: conventional-commit style, **no Co-Authored-By trailer** (repo convention).
- The live `terraform apply` rehearsal is out of scope (expired AWS session) and is recorded as pending in the ROADMAP by Task 11 — no task claims the stack was applied.

---

### Task 1: Bootstrap root — the state bucket

**Files:**
- Create: `infra/bootstrap/main.tf`

**Interfaces:**
- Produces: an S3 bucket named `marketpulse-tfstate-<account-id>` (output `state_bucket`) that Task 2's backend documentation refers to. Nothing in `infra/` references it in code — the bucket name is passed at `terraform init` time.

- [ ] **Step 1: Write `infra/bootstrap/main.tf`**

```hcl
terraform {
  required_version = ">= 1.10"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }
}

provider "aws" {
  region = var.region

  default_tags {
    tags = {
      Project   = "MarketPulse"
      ManagedBy = "Terraform"
    }
  }
}

variable "region" {
  description = "AWS region for the state bucket."
  type        = string
  default     = "ap-southeast-2"
}

data "aws_caller_identity" "current" {}

# Applied once with local state; every other root stores its state here.
resource "aws_s3_bucket" "tfstate" {
  bucket = "marketpulse-tfstate-${data.aws_caller_identity.current.account_id}"
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
  }
}

resource "aws_s3_bucket_public_access_block" "tfstate" {
  bucket = aws_s3_bucket.tfstate.id

  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

output "state_bucket" {
  description = "Pass to the main root: terraform init -backend-config=\"bucket=<this>\""
  value       = aws_s3_bucket.tfstate.bucket
}
```

- [ ] **Step 2: Format and validate**

Run: `terraform -chdir=infra/bootstrap fmt && terraform -chdir=infra/bootstrap init -backend=false && terraform -chdir=infra/bootstrap validate`
Expected: `Success! The configuration is valid.`

- [ ] **Step 3: Commit**

```bash
git add infra/bootstrap/main.tf
git commit -m "feat(infra): bootstrap root — versioned, encrypted tfstate bucket"
```

---

### Task 2: Main root skeleton — providers, variables, network, security groups

**Files:**
- Create: `infra/main.tf`, `infra/variables.tf`, `infra/network.tf`, `infra/security.tf`

**Interfaces:**
- Produces (referenced by later tasks): `aws_vpc.main`, `aws_subnet.public` (count 2), `aws_subnet.private` (count 2), `aws_security_group.alb`, `aws_security_group.api_task`, `aws_security_group.alerts_task`, `aws_security_group.rds`, `aws_security_group.mq`, variables `region`, `vpc_cidr`, `desired_count`, `db_username`, `mq_username`.

- [ ] **Step 1: Write `infra/main.tf`**

```hcl
terraform {
  required_version = ">= 1.10"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.7"
    }
  }

  # Bucket and region are supplied at init time from the bootstrap root's output:
  #   terraform init \
  #     -backend-config="bucket=$(terraform -chdir=bootstrap output -raw state_bucket)" \
  #     -backend-config="region=ap-southeast-2"
  backend "s3" {
    key          = "marketpulse/terraform.tfstate"
    use_lockfile = true
    encrypt      = true
  }
}

provider "aws" {
  region = var.region

  default_tags {
    tags = {
      Project   = "MarketPulse"
      ManagedBy = "Terraform"
    }
  }
}
```

- [ ] **Step 2: Write `infra/variables.tf`**

```hcl
variable "region" {
  description = "AWS region. The app watches the ASX; data residency follows the data."
  type        = string
  default     = "ap-southeast-2"
}

variable "vpc_cidr" {
  description = "CIDR block for the VPC."
  type        = string
  default     = "10.0.0.0/16"
}

variable "desired_count" {
  description = "Task count per ECS service. Stays 1 until images exist in ECR (slice 10)."
  type        = number
  default     = 1
}

variable "db_username" {
  description = "RDS master username. The password is RDS-managed in Secrets Manager."
  type        = string
  default     = "mpadmin"
}

variable "mq_username" {
  description = "RabbitMQ application user on the Amazon MQ broker."
  type        = string
  default     = "marketpulse"
}
```

- [ ] **Step 3: Write `infra/network.tf`**

```hcl
data "aws_availability_zones" "available" {
  state = "available"
}

locals {
  azs = slice(data.aws_availability_zones.available.names, 0, 2)
}

resource "aws_vpc" "main" {
  cidr_block           = var.vpc_cidr
  enable_dns_support   = true
  enable_dns_hostnames = true

  tags = {
    Name = "marketpulse"
  }
}

resource "aws_internet_gateway" "main" {
  vpc_id = aws_vpc.main.id

  tags = {
    Name = "marketpulse"
  }
}

# ECS tasks live here with public IPs — no NAT gateway by decision (ADR-011).
resource "aws_subnet" "public" {
  count = 2

  vpc_id                  = aws_vpc.main.id
  cidr_block              = cidrsubnet(var.vpc_cidr, 8, count.index)
  availability_zone       = local.azs[count.index]
  map_public_ip_on_launch = true

  tags = {
    Name = "marketpulse-public-${local.azs[count.index]}"
  }
}

# RDS and Amazon MQ live here. No route to the internet in either direction:
# these subnets keep the VPC's local-only default route table.
resource "aws_subnet" "private" {
  count = 2

  vpc_id            = aws_vpc.main.id
  cidr_block        = cidrsubnet(var.vpc_cidr, 8, count.index + 10)
  availability_zone = local.azs[count.index]

  tags = {
    Name = "marketpulse-private-${local.azs[count.index]}"
  }
}

resource "aws_route_table" "public" {
  vpc_id = aws_vpc.main.id

  route {
    cidr_block = "0.0.0.0/0"
    gateway_id = aws_internet_gateway.main.id
  }

  tags = {
    Name = "marketpulse-public"
  }
}

resource "aws_route_table_association" "public" {
  count = 2

  subnet_id      = aws_subnet.public[count.index].id
  route_table_id = aws_route_table.public.id
}
```

- [ ] **Step 4: Write `infra/security.tf`**

```hcl
# CloudFront's origin-facing IP ranges, maintained by AWS. Restricting the ALB
# to these means the API is only reachable through the distribution.
data "aws_ec2_managed_prefix_list" "cloudfront" {
  name = "com.amazonaws.global.cloudfront.origin-facing"
}

resource "aws_security_group" "alb" {
  name        = "marketpulse-alb"
  description = "ALB: HTTP from CloudFront origin-facing ranges only"
  vpc_id      = aws_vpc.main.id

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
}

resource "aws_security_group" "api_task" {
  name        = "marketpulse-api-task"
  description = "API tasks: container port from the ALB only"
  vpc_id      = aws_vpc.main.id

  ingress {
    description     = "App traffic from the ALB"
    from_port       = 8080
    to_port         = 8080
    protocol        = "tcp"
    security_groups = [aws_security_group.alb.id]
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }
}

resource "aws_security_group" "alerts_task" {
  name        = "marketpulse-alerts-task"
  description = "Alerts worker tasks: no ingress"
  vpc_id      = aws_vpc.main.id

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }
}

resource "aws_security_group" "rds" {
  name        = "marketpulse-rds"
  description = "SQL Server from the two task security groups only"
  vpc_id      = aws_vpc.main.id

  ingress {
    description     = "SQL Server from ECS tasks"
    from_port       = 1433
    to_port         = 1433
    protocol        = "tcp"
    security_groups = [aws_security_group.api_task.id, aws_security_group.alerts_task.id]
  }
}

resource "aws_security_group" "mq" {
  name        = "marketpulse-mq"
  description = "AMQPS from the two task security groups only"
  vpc_id      = aws_vpc.main.id

  ingress {
    description     = "AMQPS from ECS tasks"
    from_port       = 5671
    to_port         = 5671
    protocol        = "tcp"
    security_groups = [aws_security_group.api_task.id, aws_security_group.alerts_task.id]
  }
}
```

- [ ] **Step 5: Format and validate**

Run: `terraform -chdir=infra fmt -recursive && terraform -chdir=infra init -backend=false && terraform -chdir=infra validate`
Expected: `Success! The configuration is valid.`

- [ ] **Step 6: Commit**

```bash
git add infra/main.tf infra/variables.tf infra/network.tf infra/security.tf
git commit -m "feat(infra): main root — VPC without NAT, security groups, S3 backend with native lockfile"
```

---

### Task 3: ALB and ECR

**Files:**
- Create: `infra/alb.tf`, `infra/ecr.tf`

**Interfaces:**
- Consumes: `aws_vpc.main`, `aws_subnet.public`, `aws_security_group.alb` (Task 2).
- Produces: `aws_lb.main` (CloudFront origin in Task 7), `aws_lb_target_group.api` (wired into the API service in Task 6), `aws_ecr_repository.api`, `aws_ecr_repository.alerts` (image URLs in Task 6).

- [ ] **Step 1: Write `infra/alb.tf`**

```hcl
resource "aws_lb" "main" {
  name               = "marketpulse"
  load_balancer_type = "application"
  security_groups    = [aws_security_group.alb.id]
  subnets            = aws_subnet.public[*].id
}

resource "aws_lb_target_group" "api" {
  name                 = "marketpulse-api"
  port                 = 8080
  protocol             = "HTTP"
  target_type          = "ip"
  vpc_id               = aws_vpc.main.id
  deregistration_delay = 30

  # /health is the liveness endpoint (slice 8 split liveness from readiness);
  # it answers 200 whenever the process is up, downstream outages included,
  # which is exactly what a target-group check should ask.
  health_check {
    path                = "/health"
    matcher             = "200"
    interval            = 30
    healthy_threshold   = 2
    unhealthy_threshold = 3
  }
}

# HTTP-only this slice: with no custom domain there is nothing an ACM
# certificate can attest. Viewer-facing TLS terminates at CloudFront.
resource "aws_lb_listener" "http" {
  load_balancer_arn = aws_lb.main.arn
  port              = 80
  protocol          = "HTTP"

  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.api.arn
  }
}
```

- [ ] **Step 2: Write `infra/ecr.tf`**

```hcl
resource "aws_ecr_repository" "api" {
  name         = "marketpulse-api"
  force_delete = true

  image_scanning_configuration {
    scan_on_push = true
  }
}

resource "aws_ecr_repository" "alerts" {
  name         = "marketpulse-alerts"
  force_delete = true

  image_scanning_configuration {
    scan_on_push = true
  }
}
```

- [ ] **Step 3: Format and validate**

Run: `terraform -chdir=infra fmt -recursive && terraform -chdir=infra validate`
Expected: `Success! The configuration is valid.`

- [ ] **Step 4: Commit**

```bash
git add infra/alb.tf infra/ecr.tf
git commit -m "feat(infra): ALB with /health target checks; ECR repositories for both images"
```

---

### Task 4: RDS SQL Server Express and Amazon MQ for RabbitMQ

**Files:**
- Create: `infra/rds.tf`, `infra/mq.tf`

**Interfaces:**
- Consumes: `aws_subnet.private`, `aws_security_group.rds`, `aws_security_group.mq`, `var.db_username`, `var.mq_username` (Task 2).
- Produces: `aws_db_instance.main` (`.address`, `.master_user_secret[0].secret_arn`), `aws_secretsmanager_secret.mq` (`.arn`), `local.mq_host` — all consumed by Tasks 5–6.

- [ ] **Step 1: Write `infra/rds.tf`**

```hcl
resource "aws_db_subnet_group" "main" {
  name       = "marketpulse"
  subnet_ids = aws_subnet.private[*].id
}

# Express edition: license-included-free, free-tier eligible on db.t3.micro.
# Its 10 GB per-database cap is far above this dataset (ADR-011). Single-AZ,
# no backups, no deletion protection — this stack is built to be torn down.
resource "aws_db_instance" "main" {
  identifier     = "marketpulse"
  engine         = "sqlserver-ex"
  instance_class = "db.t3.micro"
  license_model  = "license-included"

  allocated_storage = 20
  storage_type      = "gp3"

  username                    = var.db_username
  manage_master_user_password = true

  db_subnet_group_name   = aws_db_subnet_group.main.name
  vpc_security_group_ids = [aws_security_group.rds.id]
  publicly_accessible    = false
  multi_az               = false

  backup_retention_period = 0
  skip_final_snapshot     = true
  deletion_protection     = false
  apply_immediately       = true
}
```

- [ ] **Step 2: Write `infra/mq.tf`**

```hcl
# Amazon MQ password rules: >= 12 chars, no commas, colons, or equals signs.
# Alphanumeric-only sidesteps all three.
resource "random_password" "mq" {
  length  = 24
  special = false
}

# The roadmap's resource list omitted the broker; the alerts pipeline is
# nonfunctional without one, so it ships here (ADR-011). mq.t3.micro is
# free-tier eligible. AMQPS-only — the app-side TLS switch is slice 10's.
resource "aws_mq_broker" "main" {
  broker_name        = "marketpulse"
  engine_type        = "RabbitMQ"
  engine_version     = "3.13"
  host_instance_type = "mq.t3.micro"
  deployment_mode    = "SINGLE_INSTANCE"

  publicly_accessible        = false
  subnet_ids                 = [aws_subnet.private[0].id]
  security_groups            = [aws_security_group.mq.id]
  auto_minor_version_upgrade = true

  user {
    username = var.mq_username
    password = random_password.mq.result
  }
}

# Broker credentials for the ECS task definitions to read at start-up.
resource "aws_secretsmanager_secret" "mq" {
  name                    = "marketpulse/mq"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "mq" {
  secret_id = aws_secretsmanager_secret.mq.id
  secret_string = jsonencode({
    username = var.mq_username
    password = random_password.mq.result
  })
}

locals {
  # instances[0].endpoints[0] is "amqps://b-....mq.<region>.amazonaws.com:5671";
  # RabbitMqOptions wants host and port separately.
  mq_host = trimsuffix(trimprefix(aws_mq_broker.main.instances[0].endpoints[0], "amqps://"), ":5671")
}
```

- [ ] **Step 3: Format and validate**

Run: `terraform -chdir=infra fmt -recursive && terraform -chdir=infra init -backend=false && terraform -chdir=infra validate`
Expected: `Success! The configuration is valid.` (re-run `init -backend=false` here because the `random` provider is newly used and must be installed).

- [ ] **Step 4: Commit**

```bash
git add infra/rds.tf infra/mq.tf
git commit -m "feat(infra): RDS SQL Server Express and Amazon MQ RabbitMQ in private subnets"
```

---

### Task 5: IAM roles and the ecs-service module

**Files:**
- Create: `infra/iam.tf`, `infra/modules/ecs-service/variables.tf`, `infra/modules/ecs-service/main.tf`, `infra/modules/ecs-service/outputs.tf`

**Interfaces:**
- Consumes: `aws_db_instance.main.master_user_secret[0].secret_arn`, `aws_secretsmanager_secret.mq.arn` (Task 4).
- Produces: `aws_iam_role.execution`, `aws_iam_role.task`; module `ecs-service` with inputs `name` (string), `cluster_id` (string), `image` (string), `cpu` (number), `memory` (number), `region` (string), `subnet_ids` (list(string)), `security_group_ids` (list(string)), `execution_role_arn` (string), `task_role_arn` (string), `desired_count` (number), `environment` (list(object({ name = string, value = string }))), `secrets` (list(object({ name = string, valueFrom = string }))), `target_group_arn` (string, default null), `container_port` (number, default null), `log_retention_days` (number, default 14). Task 6 instantiates it twice.

- [ ] **Step 1: Write `infra/iam.tf`**

```hcl
data "aws_iam_policy_document" "ecs_assume" {
  statement {
    actions = ["sts:AssumeRole"]

    principals {
      type        = "Service"
      identifiers = ["ecs-tasks.amazonaws.com"]
    }
  }
}

# Pulls images, writes logs, reads the two secrets. Nothing else.
resource "aws_iam_role" "execution" {
  name               = "marketpulse-execution"
  assume_role_policy = data.aws_iam_policy_document.ecs_assume.json
}

resource "aws_iam_role_policy_attachment" "execution" {
  role       = aws_iam_role.execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

data "aws_iam_policy_document" "execution_secrets" {
  statement {
    actions = ["secretsmanager:GetSecretValue"]
    resources = [
      aws_db_instance.main.master_user_secret[0].secret_arn,
      aws_secretsmanager_secret.mq.arn,
    ]
  }
}

resource "aws_iam_role_policy" "execution_secrets" {
  name   = "read-app-secrets"
  role   = aws_iam_role.execution.id
  policy = data.aws_iam_policy_document.execution_secrets.json
}

# The app calls no AWS APIs today; the role exists so a future permission
# lands on the task role rather than the execution role by path of least
# resistance.
resource "aws_iam_role" "task" {
  name               = "marketpulse-task"
  assume_role_policy = data.aws_iam_policy_document.ecs_assume.json
}
```

- [ ] **Step 2: Write `infra/modules/ecs-service/variables.tf`**

```hcl
variable "name" {
  description = "Service, task family, and container name."
  type        = string
}

variable "cluster_id" {
  description = "ECS cluster to run in."
  type        = string
}

variable "image" {
  description = "Full image reference including tag."
  type        = string
}

variable "cpu" {
  description = "Fargate task CPU units."
  type        = number
}

variable "memory" {
  description = "Fargate task memory in MiB."
  type        = number
}

variable "region" {
  description = "Region, for the awslogs driver."
  type        = string
}

variable "subnet_ids" {
  description = "Subnets for the task ENIs."
  type        = list(string)
}

variable "security_group_ids" {
  description = "Security groups for the task ENIs."
  type        = list(string)
}

variable "execution_role_arn" {
  description = "Role ECS uses to pull the image, write logs, read secrets."
  type        = string
}

variable "task_role_arn" {
  description = "Role the application code assumes."
  type        = string
}

variable "desired_count" {
  description = "Number of tasks."
  type        = number
}

variable "environment" {
  description = "Plain environment variables."
  type = list(object({
    name  = string
    value = string
  }))
  default = []
}

variable "secrets" {
  description = "Secrets Manager references injected as environment variables."
  type = list(object({
    name      = string
    valueFrom = string
  }))
  default = []
}

variable "target_group_arn" {
  description = "Target group to register with. Null for services without a load balancer."
  type        = string
  default     = null
}

variable "container_port" {
  description = "Container port exposed to the target group. Null when target_group_arn is null."
  type        = number
  default     = null
}

variable "log_retention_days" {
  description = "CloudWatch log retention."
  type        = number
  default     = 14
}
```

- [ ] **Step 3: Write `infra/modules/ecs-service/main.tf`**

```hcl
resource "aws_cloudwatch_log_group" "this" {
  name              = "/ecs/${var.name}"
  retention_in_days = var.log_retention_days
}

resource "aws_ecs_task_definition" "this" {
  family                   = var.name
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = var.cpu
  memory                   = var.memory
  execution_role_arn       = var.execution_role_arn
  task_role_arn            = var.task_role_arn

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }

  container_definitions = jsonencode([
    {
      name      = var.name
      image     = var.image
      essential = true
      portMappings = var.container_port == null ? [] : [
        {
          containerPort = var.container_port
          protocol      = "tcp"
        }
      ]
      environment = var.environment
      secrets     = var.secrets
      logConfiguration = {
        logDriver = "awslogs"
        options = {
          awslogs-group         = aws_cloudwatch_log_group.this.name
          awslogs-region        = var.region
          awslogs-stream-prefix = "ecs"
        }
      }
    }
  ])
}

# wait_for_steady_state stays off: the first apply happens against empty ECR
# repositories, and the service reaching steady state is slice 10's concern
# (bootstrap order: apply, push images, tasks start).
resource "aws_ecs_service" "this" {
  name            = var.name
  cluster         = var.cluster_id
  task_definition = aws_ecs_task_definition.this.arn
  desired_count   = var.desired_count
  launch_type     = "FARGATE"

  network_configuration {
    subnets          = var.subnet_ids
    security_groups  = var.security_group_ids
    assign_public_ip = true
  }

  dynamic "load_balancer" {
    for_each = var.target_group_arn == null ? [] : [var.target_group_arn]

    content {
      target_group_arn = load_balancer.value
      container_name   = var.name
      container_port   = var.container_port
    }
  }
}
```

- [ ] **Step 4: Write `infra/modules/ecs-service/outputs.tf`**

```hcl
output "service_name" {
  description = "ECS service name, for slice 10's deploy workflow."
  value       = aws_ecs_service.this.name
}

output "log_group" {
  description = "CloudWatch log group receiving container output."
  value       = aws_cloudwatch_log_group.this.name
}
```

- [ ] **Step 5: Format and validate**

Run: `terraform -chdir=infra fmt -recursive && terraform -chdir=infra validate`
Expected: `Success! The configuration is valid.`

- [ ] **Step 6: Commit**

```bash
git add infra/iam.tf infra/modules/ecs-service
git commit -m "feat(infra): least-privilege task roles and the reusable ecs-service module"
```

---

### Task 6: ECS cluster and the two service instances

**Files:**
- Create: `infra/ecs.tf`, `infra/outputs.tf`

**Interfaces:**
- Consumes: everything Tasks 2–5 produced — subnets, SGs, target group, ECR repos, `aws_db_instance.main`, `aws_secretsmanager_secret.mq`, `local.mq_host`, IAM roles, module `ecs-service`.
- Produces: `module.api`, `module.alerts`, root outputs (`cloudfront_url` arrives in Task 7). The container env contract (`DB_HOST`/`DB_USERNAME`/`DB_PASSWORD` composed into `ConnectionStrings__MarketPulse` by the entrypoint) is implemented by Task 8's Dockerfiles — the names must match exactly.

- [ ] **Step 1: Write `infra/ecs.tf`**

```hcl
resource "aws_ecs_cluster" "main" {
  name = "marketpulse"

  # Container Insights is real money at this scale; the OTel pipeline from
  # slice 8 is the observability story, not CloudWatch agents.
  setting {
    name  = "containerInsights"
    value = "disabled"
  }
}

locals {
  db_secret_arn = aws_db_instance.main.master_user_secret[0].secret_arn

  # DB_HOST/DB_USERNAME/DB_PASSWORD are composed into
  # ConnectionStrings__MarketPulse by each image's docker-entrypoint.sh —
  # the password never appears in a task definition or in state-readable
  # plain environment.
  shared_environment = [
    { name = "DB_HOST", value = aws_db_instance.main.address },
    { name = "RabbitMq__HostName", value = local.mq_host },
    { name = "RabbitMq__Port", value = "5671" },
  ]

  shared_secrets = [
    { name = "DB_USERNAME", valueFrom = "${local.db_secret_arn}:username::" },
    { name = "DB_PASSWORD", valueFrom = "${local.db_secret_arn}:password::" },
    { name = "RabbitMq__UserName", valueFrom = "${aws_secretsmanager_secret.mq.arn}:username::" },
    { name = "RabbitMq__Password", valueFrom = "${aws_secretsmanager_secret.mq.arn}:password::" },
  ]
}

module "api" {
  source = "./modules/ecs-service"

  name               = "marketpulse-api"
  cluster_id         = aws_ecs_cluster.main.id
  image              = "${aws_ecr_repository.api.repository_url}:latest"
  cpu                = 256
  memory             = 512
  region             = var.region
  subnet_ids         = aws_subnet.public[*].id
  security_group_ids = [aws_security_group.api_task.id]
  execution_role_arn = aws_iam_role.execution.arn
  task_role_arn      = aws_iam_role.task.arn
  desired_count      = var.desired_count
  target_group_arn   = aws_lb_target_group.api.arn
  container_port     = 8080

  environment = concat(local.shared_environment, [
    { name = "ASPNETCORE_ENVIRONMENT", value = "Production" },
    { name = "ASPNETCORE_URLS", value = "http://0.0.0.0:8080" },
  ])
  secrets = local.shared_secrets
}

module "alerts" {
  source = "./modules/ecs-service"

  name               = "marketpulse-alerts"
  cluster_id         = aws_ecs_cluster.main.id
  image              = "${aws_ecr_repository.alerts.repository_url}:latest"
  cpu                = 256
  memory             = 512
  region             = var.region
  subnet_ids         = aws_subnet.public[*].id
  security_group_ids = [aws_security_group.alerts_task.id]
  execution_role_arn = aws_iam_role.execution.arn
  task_role_arn      = aws_iam_role.task.arn
  desired_count      = var.desired_count

  environment = concat(local.shared_environment, [
    { name = "DOTNET_ENVIRONMENT", value = "Production" },
  ])
  secrets = local.shared_secrets
}
```

- [ ] **Step 2: Write `infra/outputs.tf`**

```hcl
output "alb_dns_name" {
  description = "Origin address CloudFront forwards /api/* and /hubs/* to."
  value       = aws_lb.main.dns_name
}

output "ecr_api_url" {
  description = "Push marketpulse-api images here (slice 10)."
  value       = aws_ecr_repository.api.repository_url
}

output "ecr_alerts_url" {
  description = "Push marketpulse-alerts images here (slice 10)."
  value       = aws_ecr_repository.alerts.repository_url
}

output "rds_address" {
  description = "SQL Server endpoint (private; reachable from the task SGs only)."
  value       = aws_db_instance.main.address
}

output "mq_host" {
  description = "RabbitMQ host (private; AMQPS 5671 from the task SGs only)."
  value       = local.mq_host
}
```

- [ ] **Step 3: Format and validate**

Run: `terraform -chdir=infra fmt -recursive && terraform -chdir=infra init -backend=false && terraform -chdir=infra validate`
Expected: `Success! The configuration is valid.` (`init` again so the local module is discovered).

- [ ] **Step 4: Commit**

```bash
git add infra/ecs.tf infra/outputs.tf
git commit -m "feat(infra): ECS cluster with api and alerts services; secrets-wired task env"
```

---

### Task 7: SPA bucket and the dual-origin CloudFront distribution

**Files:**
- Create: `infra/spa.tf`, `infra/spa-fallback.js`
- Modify: `docs/superpowers/specs/2026-08-07-infrastructure-as-code-design.md` (record one deviation)

**Interfaces:**
- Consumes: `aws_lb.main.dns_name` (Task 3).
- Produces: `aws_s3_bucket.spa` (slice 10 uploads the SPA build here), `aws_cloudfront_distribution.main`, output `cloudfront_url`.

**Design note (spec deviation to record):** the spec says "SPA 403/404 → `/index.html` fallback". Distribution-level `custom_error_response` would also rewrite **API** 403s into a 200 `index.html` — breaking real auth failures. The fallback is therefore a CloudFront Function on the default (S3) behavior's viewer-request that rewrites extension-less paths to `/index.html`; `/api/*` and `/hubs/*` never reach it. Step 5 records this in the spec.

- [ ] **Step 1: Write `infra/spa-fallback.js`**

```javascript
// SPA route fallback for the S3 origin only. Ordered behaviors send /api/*
// and /hubs/* to the ALB before this function runs; the guards are belt and
// braces. Anything with a file extension (assets) passes through untouched.
function handler(event) {
    var request = event.request;
    var uri = request.uri;
    if (!uri.startsWith('/api/') && !uri.startsWith('/hubs/') && !uri.includes('.')) {
        request.uri = '/index.html';
    }
    return request;
}
```

- [ ] **Step 2: Write `infra/spa.tf`**

```hcl
resource "aws_s3_bucket" "spa" {
  bucket_prefix = "marketpulse-spa-"
  force_destroy = true
}

resource "aws_s3_bucket_public_access_block" "spa" {
  bucket = aws_s3_bucket.spa.id

  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_cloudfront_origin_access_control" "spa" {
  name                              = "marketpulse-spa"
  origin_access_control_origin_type = "s3"
  signing_behavior                  = "always"
  signing_protocol                  = "sigv4"
}

resource "aws_cloudfront_function" "spa_fallback" {
  name    = "marketpulse-spa-fallback"
  runtime = "cloudfront-js-2.0"
  publish = true
  code    = file("${path.module}/spa-fallback.js")
}

locals {
  spa_origin_id = "spa-s3"
  api_origin_id = "api-alb"

  # AWS managed policy IDs — stable, documented constants.
  cache_policy_caching_optimized = "658327ea-f89d-4fab-a63d-7e88639e58f6"
  cache_policy_caching_disabled  = "4135ea2d-6df8-44a3-9df3-4b5a84be39ad"
  origin_request_all_viewer      = "216adef6-5c7f-47e4-b989-5492eafa07d3"
}

# One distribution fronts both origins: the SPA and the API share an origin
# from the browser's point of view, so the auth cookie needs no SameSite=None
# and there is no CORS story to get wrong (ADR-011).
resource "aws_cloudfront_distribution" "main" {
  enabled             = true
  default_root_object = "index.html"
  # PriceClass_100 excludes Australia; the app watches the ASX.
  price_class = "PriceClass_All"

  origin {
    domain_name              = aws_s3_bucket.spa.bucket_regional_domain_name
    origin_id                = local.spa_origin_id
    origin_access_control_id = aws_cloudfront_origin_access_control.spa.id
  }

  origin {
    domain_name = aws_lb.main.dns_name
    origin_id   = local.api_origin_id

    custom_origin_config {
      http_port              = 80
      https_port             = 443
      origin_protocol_policy = "http-only"
      origin_ssl_protocols   = ["TLSv1.2"]
    }
  }

  default_cache_behavior {
    target_origin_id       = local.spa_origin_id
    viewer_protocol_policy = "redirect-to-https"
    allowed_methods        = ["GET", "HEAD"]
    cached_methods         = ["GET", "HEAD"]
    cache_policy_id        = local.cache_policy_caching_optimized
    compress               = true

    function_association {
      event_type   = "viewer-request"
      function_arn = aws_cloudfront_function.spa_fallback.arn
    }
  }

  ordered_cache_behavior {
    path_pattern             = "/api/*"
    target_origin_id         = local.api_origin_id
    viewer_protocol_policy   = "redirect-to-https"
    allowed_methods          = ["GET", "HEAD", "OPTIONS", "PUT", "POST", "PATCH", "DELETE"]
    cached_methods           = ["GET", "HEAD"]
    cache_policy_id          = local.cache_policy_caching_disabled
    origin_request_policy_id = local.origin_request_all_viewer
  }

  # SignalR: CloudFront passes WebSocket upgrades through automatically.
  ordered_cache_behavior {
    path_pattern             = "/hubs/*"
    target_origin_id         = local.api_origin_id
    viewer_protocol_policy   = "redirect-to-https"
    allowed_methods          = ["GET", "HEAD", "OPTIONS", "PUT", "POST", "PATCH", "DELETE"]
    cached_methods           = ["GET", "HEAD"]
    cache_policy_id          = local.cache_policy_caching_disabled
    origin_request_policy_id = local.origin_request_all_viewer
  }

  restrictions {
    geo_restriction {
      restriction_type = "none"
    }
  }

  viewer_certificate {
    cloudfront_default_certificate = true
  }
}

data "aws_iam_policy_document" "spa_bucket" {
  statement {
    actions   = ["s3:GetObject"]
    resources = ["${aws_s3_bucket.spa.arn}/*"]

    principals {
      type        = "Service"
      identifiers = ["cloudfront.amazonaws.com"]
    }

    condition {
      test     = "StringEquals"
      variable = "AWS:SourceArn"
      values   = [aws_cloudfront_distribution.main.arn]
    }
  }
}

resource "aws_s3_bucket_policy" "spa" {
  bucket = aws_s3_bucket.spa.id
  policy = data.aws_iam_policy_document.spa_bucket.json
}
```

- [ ] **Step 3: Add the CloudFront output to `infra/outputs.tf`** (append)

```hcl
output "cloudfront_url" {
  description = "The application's front door."
  value       = "https://${aws_cloudfront_distribution.main.domain_name}"
}

output "spa_bucket" {
  description = "Upload the dashboard build here (slice 10)."
  value       = aws_s3_bucket.spa.bucket
}
```

- [ ] **Step 4: Format and validate**

Run: `terraform -chdir=infra fmt -recursive && terraform -chdir=infra validate`
Expected: `Success! The configuration is valid.`

- [ ] **Step 5: Record the fallback deviation in the spec**

In `docs/superpowers/specs/2026-08-07-infrastructure-as-code-design.md`, in the **In scope** bullet listing the CloudFront pieces, replace the text `SPA 403/404 → /index.html fallback` with `SPA route fallback to /index.html via a CloudFront Function on the S3 behavior (distribution-wide custom_error_response would also rewrite API 403s — found during implementation)`.

- [ ] **Step 6: Commit**

```bash
git add infra/spa.tf infra/spa-fallback.js infra/outputs.tf docs/superpowers/specs/2026-08-07-infrastructure-as-code-design.md
git commit -m "feat(infra): private SPA bucket behind dual-origin CloudFront with function-based route fallback"
```

---

### Task 8: Alerts Dockerfile and entrypoint-composed connection strings

**Files:**
- Create: `src/MarketPulse.Alerts/Dockerfile`, `src/MarketPulse.Alerts/docker-entrypoint.sh`, `src/MarketPulse.Api/docker-entrypoint.sh`
- Modify: `src/MarketPulse.Api/Dockerfile`, `.github/workflows/ci.yml` (docker job only)

**Interfaces:**
- Consumes: the env contract from Task 6 (`DB_HOST`, `DB_USERNAME`, `DB_PASSWORD` present in cloud task definitions).
- Produces: two buildable images whose entrypoints compose `ConnectionStrings__MarketPulse` when `DB_HOST` is set and behave exactly as before when it is not (local compose/CI unaffected).

- [ ] **Step 1: Write `src/MarketPulse.Api/docker-entrypoint.sh`**

```sh
#!/bin/sh
set -e

# In AWS the task definition injects DB_HOST plus DB_USERNAME/DB_PASSWORD
# from Secrets Manager; the full connection string is composed here so the
# password never sits in a task definition. Locally DB_HOST is unset and
# configuration comes from appsettings/environment as always.
if [ -n "$DB_HOST" ]; then
  export ConnectionStrings__MarketPulse="Server=${DB_HOST},1433;Database=MarketPulse;User Id=${DB_USERNAME};Password=${DB_PASSWORD};TrustServerCertificate=True"
fi

exec dotnet MarketPulse.Api.dll
```

- [ ] **Step 2: Write `src/MarketPulse.Alerts/docker-entrypoint.sh`**

```sh
#!/bin/sh
set -e

# In AWS the task definition injects DB_HOST plus DB_USERNAME/DB_PASSWORD
# from Secrets Manager; the full connection string is composed here so the
# password never sits in a task definition. Locally DB_HOST is unset and
# configuration comes from appsettings/environment as always.
if [ -n "$DB_HOST" ]; then
  export ConnectionStrings__MarketPulse="Server=${DB_HOST},1433;Database=MarketPulse;User Id=${DB_USERNAME};Password=${DB_PASSWORD};TrustServerCertificate=True"
fi

exec dotnet MarketPulse.Alerts.dll
```

- [ ] **Step 3: Update `src/MarketPulse.Api/Dockerfile`**

Replace the final two lines (`EXPOSE 8080` / `ENTRYPOINT ["dotnet", "MarketPulse.Api.dll"]`) with:

```dockerfile
COPY src/MarketPulse.Api/docker-entrypoint.sh .
RUN chmod +x docker-entrypoint.sh
EXPOSE 8080
ENTRYPOINT ["./docker-entrypoint.sh"]
```

(The `COPY src/ src/` layer already brings the script into the build stage, but copying it explicitly into the runtime stage from the build context keeps the runtime image's provenance obvious.)

- [ ] **Step 4: Write `src/MarketPulse.Alerts/Dockerfile`** (mirrors the API's shape)

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/MarketPulse.Domain/*.csproj src/MarketPulse.Domain/
COPY src/MarketPulse.Application/*.csproj src/MarketPulse.Application/
COPY src/MarketPulse.Infrastructure/*.csproj src/MarketPulse.Infrastructure/
COPY src/MarketPulse.Alerts/*.csproj src/MarketPulse.Alerts/
RUN dotnet restore src/MarketPulse.Alerts/MarketPulse.Alerts.csproj

COPY src/ src/
RUN dotnet publish src/MarketPulse.Alerts/MarketPulse.Alerts.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
COPY src/MarketPulse.Alerts/docker-entrypoint.sh .
RUN chmod +x docker-entrypoint.sh
ENTRYPOINT ["./docker-entrypoint.sh"]
```

Before running the build, check `src/MarketPulse.Alerts/MarketPulse.Alerts.csproj` `<ProjectReference>` entries: if it references only a subset of Domain/Application/Infrastructure, the restore COPY lines above still work (extra csproj copies are harmless) — do not trim them; matching the API Dockerfile's shape is the point.

- [ ] **Step 5: Build both images locally to verify**

Run: `docker build -f src/MarketPulse.Api/Dockerfile -t marketpulse-api:local . && docker build -f src/MarketPulse.Alerts/Dockerfile -t marketpulse-alerts:local .`
Expected: both builds succeed. If the Docker daemon is not running locally, note it and rely on Step 6's CI coverage — do not skip Step 6.

- [ ] **Step 6: Add the Alerts image to the CI docker job**

In `.github/workflows/ci.yml`, the `docker` job currently ends with one `docker/build-push-action@v6` step for the API image. Add a second step after it:

```yaml
      - uses: docker/build-push-action@v6
        with:
          context: .
          file: src/MarketPulse.Alerts/Dockerfile
          push: false
          tags: marketpulse-alerts:ci
```

- [ ] **Step 7: Commit**

```bash
git add src/MarketPulse.Api/Dockerfile src/MarketPulse.Api/docker-entrypoint.sh src/MarketPulse.Alerts/Dockerfile src/MarketPulse.Alerts/docker-entrypoint.sh .github/workflows/ci.yml
git commit -m "feat(docker): alerts worker image; entrypoints compose the connection string from task secrets"
```

---

### Task 9: CI infra job

**Files:**
- Modify: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: both Terraform roots (Tasks 1–7).
- Produces: a CI gate proving `fmt`/`validate` cleanliness with no cloud credentials.

- [ ] **Step 1: Add the `infra` job to `.github/workflows/ci.yml`** (alongside `backend`/`frontend`, no `needs`)

```yaml
  infra:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: hashicorp/setup-terraform@v3
        with:
          terraform_version: '1.15.7'
      - run: terraform fmt -check -recursive
        working-directory: infra
      # -backend=false: validation needs providers, not state or credentials.
      - run: terraform init -backend=false && terraform validate
        working-directory: infra/bootstrap
      - run: terraform init -backend=false && terraform validate
        working-directory: infra
```

- [ ] **Step 2: Sanity-check the workflow parses**

Run: `ruby -ryaml -e "YAML.load_file('.github/workflows/ci.yml'); puts 'ok'"` (macOS ships Ruby; if unavailable, `python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/ci.yml')); print('ok')"`).
Expected: `ok`.

- [ ] **Step 3: Run the same commands locally**

Run: `cd infra && terraform fmt -check -recursive && cd bootstrap && terraform validate && cd .. && terraform validate`
Expected: exit 0, both roots valid (init already done in earlier tasks).

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: infra job — terraform fmt and validate on both roots, credential-free"
```

---

### Task 10: infra README, cost model, ADR-011

**Files:**
- Create: `infra/README.md`, `docs/cost-model.md`, `docs/adr/011-infrastructure-shape.md`

**Interfaces:**
- Consumes: the whole `infra/` tree; the spec's decisions 2–6.
- Produces: the documents Task 11's README/ROADMAP edits link to.

- [ ] **Step 1: Write `infra/README.md`**

```markdown
# infra/ — Terraform

Two roots. `bootstrap/` creates the S3 state bucket and is applied once with
local state; everything else lives in this root and stores state in that
bucket (S3 native locking via `use_lockfile` — no DynamoDB table).

## Bootstrap order

```bash
# 0. Credentials: aws login (SSO) — everything below needs them.

# 1. Once ever: the state bucket.
terraform -chdir=bootstrap init
terraform -chdir=bootstrap apply

# 2. The stack.
terraform init \
  -backend-config="bucket=$(terraform -chdir=bootstrap output -raw state_bucket)" \
  -backend-config="region=ap-southeast-2"
terraform apply
```

The first apply succeeds against **empty ECR repositories**: both ECS
services are created with `wait_for_steady_state` off and will start tasks
as soon as images arrive. Pushing images, uploading the SPA build to the
bucket, and flipping the services live are slice 10's deploy pipeline.

## What exists after apply

VPC (2 AZ, public subnets for ECS tasks — no NAT, see ADR-011; private
subnets for data), ALB (HTTP, health-checked on `/health`), ECS cluster
with `marketpulse-api` (behind the ALB) and `marketpulse-alerts` services,
ECR ×2, RDS SQL Server Express (`db.t3.micro`, master password in
Secrets Manager), Amazon MQ for RabbitMQ (`mq.t3.micro`, AMQPS), private
SPA bucket behind CloudFront with `/api/*` and `/hubs/*` routed to the ALB.
`terraform output cloudfront_url` is the front door.

## Variables

| Variable | Default | Why you'd change it |
|---|---|---|
| `region` | `ap-southeast-2` | You aren't watching the ASX |
| `vpc_cidr` | `10.0.0.0/16` | Peering collisions |
| `desired_count` | `1` | Scale-out demos; `0` parks compute |
| `db_username` | `mpadmin` | Taste |
| `mq_username` | `marketpulse` | Taste |

## Teardown

`terraform destroy` removes everything including data (`force_delete`/
`force_destroy`/`skip_final_snapshot` are all deliberate — see
[docs/cost-model.md](../docs/cost-model.md)). The bootstrap bucket survives
unless you also destroy `bootstrap/` (it versions state; empty it first).

## Known limits (owned by later slices)

- Images and SPA upload: slice 10. App-side AMQPS (`RabbitMqOptions` TLS
  switch): slice 10. `Otel__OtlpEndpoint` is unset — no collector in the
  cloud yet; slice 10 decides the exporter story.
- ALB is HTTP-only origin-side; custom domain, ACM, WAF, NAT revisit:
  slice 11.
```

- [ ] **Step 2: Write `docs/cost-model.md`** — the table below is the required skeleton; verify each price against current ap-southeast-2 pricing pages while writing and correct any that drifted, keeping the "approximate, checked 2026-08" framing:

```markdown
# Cost model

Estimates for `ap-southeast-2`, approximate, checked 2026-08. Two columns
because the strategy has two phases: the first 12 months ride the AWS free
tier; after that (or past its caps) the same stack has a real monthly cost.

## Monthly estimate

| Resource | Sizing | Free tier (first 12 mo) | Post-free-tier |
|---|---|---|---|
| RDS SQL Server Express | db.t3.micro, 20 GB gp3, single-AZ | $0 (750 h + 20 GB) | ~US$30 |
| Amazon MQ RabbitMQ | mq.t3.micro, single-instance | $0 (750 h) | ~US$32 |
| ECS Fargate | 2 × (0.25 vCPU / 0.5 GB), 730 h | no free tier — ~US$22 | ~US$22 |
| ALB | 1, idle-ish | $0 (750 h + 15 LCU) | ~US$25 |
| CloudFront | < 1 TB out, < 10 M req | $0 (always-free tier) | ~$0 |
| S3 (SPA + tfstate) | < 1 GB | $0 (5 GB) | < US$1 |
| ECR | 2 repos, < 500 MB | $0 (500 MB) | < US$1 |
| Secrets Manager | 2 secrets | ~US$0.80 (no free tier) | ~US$0.80 |
| CloudWatch Logs | 14-day retention, low volume | $0 (5 GB) | ~US$3 |
| **Total** | | **~US$23/mo** | **~US$115/mo** |

## The three deliberate cost decisions (ADR-011)

1. **No NAT gateway** (~US$45/mo saved): ECS tasks sit in public subnets
   with strict security groups instead of private-plus-NAT.
2. **SQL Server Express** ($0 license): free-tier eligible and
   license-included-free forever; the 10 GB/database cap is far above this
   dataset.
3. **Single-AZ everything**: RDS single-AZ, single-instance broker,
   `desired_count = 1`. Availability is a paid feature this portfolio
   project doesn't need.

## Teardown strategy

The stack is built to be destroyed between demo sessions: no deletion
protection, no final snapshots, force-destroy on buckets and repositories.
Parked cost is ~$0 (S3/ECR pennies); `terraform apply` from scratch
restores everything except data — which the app reseeds.
```

- [ ] **Step 3: Write `docs/adr/011-infrastructure-shape.md`** — follow the house ADR format (read `docs/adr/010-market-data-feed.md` first for the exact heading structure: Status/Context/Decision/Consequences with rejected alternatives inline). Content to cover, one Decision subsection each:

1. **State**: S3 backend + native lockfile via a bootstrap root; rejected DynamoDB lock table (superseded ≥ 1.10) and Terraform Cloud (external account dependency).
2. **No NAT**: tasks in public subnets with strict SGs, ALB ingress locked to CloudFront's origin-facing prefix list; rejected NAT gateway (~US$45/mo idle) and VPC endpoints (4+ × ~US$8/mo exceeds the NAT they replace); flagged for slice 11 revisit.
3. **One CloudFront distribution, two origins**: same-origin SPA + API preserves ADR-003's cookie posture, no CORS, no mixed content without owning a domain; ALB HTTP-only this slice; SPA fallback via CloudFront Function not `custom_error_response` (which would rewrite API 403s); rejected separate API subdomain + ACM (no domain) and direct ALB exposure.
4. **RDS SQL Server Express db.t3.micro**: license-free, free-tier; RDS-managed master password (no secret in state's plain env or code); rejected Web/Standard (license cost) and SQL-on-Fargate (state on ephemeral compute).
5. **Amazon MQ for RabbitMQ despite roadmap omission**: the alerts pipeline needs a broker or the stack cannot run the system; mq.t3.micro free-tier; AMQPS-only with the app-side TLS switch recorded as slice 10 work; rejected RabbitMQ-on-ECS (no persistence story) and SQS/SNS (protocol rewrite, not an IaC slice).

Consequences must include: the public-subnet posture trade-off, the empty-repo first apply (tasks start only after slice 10 pushes images), and that the live apply rehearsal is pending credentials.

- [ ] **Step 4: Commit**

```bash
git add infra/README.md docs/cost-model.md docs/adr/011-infrastructure-shape.md
git commit -m "docs: infra README, cost model, ADR-011 — the infrastructure shape"
```

---

### Task 11: README and ROADMAP honesty pass

**Files:**
- Modify: `README.md`, `docs/ROADMAP.md`

**Interfaces:**
- Consumes: everything shipped in Tasks 1–10.

- [ ] **Step 1: Update `README.md` deferred claims**

- In the deferred-claims register area of `docs/ROADMAP.md` (see Step 2) and in the README: the `docs/cost-model.md` row's resolution becomes **Resolved in 9** with a link, and the repo-tree comment for `infra/` (`# Terraform (ECS, Lambda, RDS, S3, IAM, VPC)`) becomes `# Terraform (VPC, ECS, RDS, MQ, S3+CloudFront) — Lambda arrives with slice 10`.
- Search the README for other now-stale infra claims (`grep -n -i "terraform\|infra/" README.md`) and align any that assert things slice 9 didn't build (e.g. "state in S3 with locking" is now true — leave it; blue-green remains slice 10 — leave labelled as roadmap).

- [ ] **Step 2: Update `docs/ROADMAP.md`**

- Move slice 9 from **Remaining slices** into the shipped table with an honest row: what shipped (both roots, the ecs-service module, dual-origin CloudFront with the function-based SPA fallback, RDS Express, Amazon MQ — noting the roadmap's original resource list omitted the broker and why it was added, the Alerts Dockerfile + entrypoint composition, CI infra job, cost model, ADR-011) **and what did not run**: the live `terraform apply` rehearsal is pending refreshed AWS credentials — configuration is `fmt`/`validate`-clean and CI-gated, but "reproducible from scratch" remains asserted-not-demonstrated until the rehearsal runs (clean apply, CloudFront URL serves the fallback, clean destroy). Same style as slice 6's rate-limited caveat.
- Update the phase-5 status row (line ~40) to reflect IaC done-pending-rehearsal, pipeline (10) remaining.
- Slice 10's entry gains the two follow-ups this slice discovered: `RabbitMqOptions` TLS switch for AMQPS, and the cloud OTel exporter decision (`Otel__OtlpEndpoint` currently unset in task definitions).
- Update the `docs/cost-model.md` deferred-claims row to **Resolved in 9**.

- [ ] **Step 3: Commit**

```bash
git add README.md docs/ROADMAP.md
git commit -m "docs: close slice 9 — IaC shipped, apply rehearsal pending credentials"
```

---

## Verification (slice level)

1. `terraform -chdir=infra/bootstrap validate` and `terraform -chdir=infra validate` — clean.
2. `terraform -chdir=infra fmt -check -recursive` — no diffs.
3. `docker build` both images (or CI green if no local daemon).
4. `git log --oneline` — one commit per task, no Co-Authored-By trailers.
5. ROADMAP/README/spec/ADR all tell the same story, including what did **not** run.
