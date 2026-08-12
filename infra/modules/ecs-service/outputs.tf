output "service_name" {
  description = "ECS service name, for slice 10's deploy workflow."
  value       = aws_ecs_service.this.name
}

output "log_group" {
  description = "CloudWatch log group receiving container output."
  value       = aws_cloudwatch_log_group.this.name
}
