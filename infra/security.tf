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
