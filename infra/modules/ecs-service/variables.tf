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
