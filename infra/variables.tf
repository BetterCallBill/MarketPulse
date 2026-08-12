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
