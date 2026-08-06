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
