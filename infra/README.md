# infra/ — Terraform

Two roots. `bootstrap/` creates the S3 state bucket and is applied once with
local state; everything else lives in this root and stores state in that
bucket (S3 native locking via `use_lockfile` — no DynamoDB table).

## Bootstrap order

```bash
# 0. Credentials: aws login (SSO) — everything below needs them.

# 1. Once ever: the state bucket.
terraform -chdir=bootstrap init
terraform -chdir=bootstrap apply

# 2. The stack.
terraform init \
  -backend-config="bucket=$(terraform -chdir=bootstrap output -raw state_bucket)" \
  -backend-config="region=ap-southeast-2"
terraform apply
```

The first apply succeeds against **empty ECR repositories**: both ECS
services are created with `wait_for_steady_state` off and will start tasks
as soon as images arrive. Pushing images, uploading the SPA build to the
bucket, and flipping the services live are slice 10's deploy pipeline.

## What exists after apply

VPC (2 AZ, public subnets for ECS tasks — no NAT, see
[ADR-011](../docs/adr/011-infrastructure-shape.md); private subnets for
data), ALB (HTTP, health-checked on `/health`), ECS cluster with
`marketpulse-api` (behind the ALB) and `marketpulse-alerts` services,
ECR ×2, RDS SQL Server Express (`db.t3.micro`, master password in
Secrets Manager), Amazon MQ for RabbitMQ (`mq.t3.micro`, AMQPS), private
SPA bucket behind CloudFront with `/api/*` and `/hubs/*` routed to the ALB.
`terraform output cloudfront_url` is the front door.

## Variables

| Variable | Default | Why you'd change it |
|---|---|---|
| `region` | `ap-southeast-2` | You aren't watching the ASX |
| `vpc_cidr` | `10.0.0.0/16` | Peering collisions |
| `desired_count` | `1` | Scale-out demos; `0` parks compute |
| `db_username` | `mpadmin` | Taste |
| `mq_username` | `marketpulse` | Taste |

## Teardown

`terraform destroy` removes everything including data (`force_delete`/
`force_destroy`/`skip_final_snapshot` are all deliberate — see
[docs/cost-model.md](../docs/cost-model.md)). The bootstrap bucket survives
unless you also destroy `bootstrap/` (it versions state; empty it first).

## Known limits (owned by later slices)

- Images and SPA upload: slice 10. App-side AMQPS (`RabbitMqOptions` TLS
  switch): slice 10. `Otel__OtlpEndpoint` is unset — no collector in the
  cloud yet; slice 10 decides the exporter story.
- ALB is HTTP-only origin-side; custom domain, ACM, WAF, NAT revisit:
  slice 11.
