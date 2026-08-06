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
