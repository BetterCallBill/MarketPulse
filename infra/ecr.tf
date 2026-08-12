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
