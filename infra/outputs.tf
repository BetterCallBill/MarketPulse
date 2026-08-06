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
