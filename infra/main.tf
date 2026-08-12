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
