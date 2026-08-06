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
