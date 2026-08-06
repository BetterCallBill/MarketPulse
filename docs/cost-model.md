# Cost model

Estimates for `ap-southeast-2`, approximate, checked 2026-08. Two columns
because the strategy has two phases: the first 12 months ride the AWS free
tier; after that (or past its caps) the same stack has a real monthly cost.

## Monthly estimate

| Resource | Sizing | Free tier (first 12 mo) | Post-free-tier |
|---|---|---|---|
| RDS SQL Server Express | db.t3.micro, 20 GB gp3, single-AZ | $0 (750 h + 20 GB) | ~US$30 |
| Amazon MQ RabbitMQ | mq.t3.micro, single-instance | $0 (750 h) | ~US$32 |
| ECS Fargate | 2 × (0.25 vCPU / 0.5 GB), 730 h | no free tier — ~US$22 | ~US$22 |
| ALB | 1, idle-ish | $0 (750 h + 15 LCU) | ~US$25 |
| CloudFront | < 1 TB out, < 10 M req | $0 (always-free tier) | ~$0 |
| S3 (SPA + tfstate) | < 1 GB | $0 (5 GB) | < US$1 |
| ECR | 2 repos, < 500 MB | $0 (500 MB) | < US$1 |
| Secrets Manager | 2 secrets | ~US$0.80 (no free tier) | ~US$0.80 |
| CloudWatch Logs | 14-day retention, low volume | $0 (5 GB) | ~US$3 |
| **Total** | | **~US$23/mo** | **~US$115/mo** |

Prices are list, rounded, and will drift; the shape of the argument — which
lines are free, which line the free tier never covers (Fargate), and which
single resource would dominate if added (NAT) — is the durable part.

## The three deliberate cost decisions (ADR-011)

1. **No NAT gateway** (~US$45/mo saved): ECS tasks sit in public subnets
   with strict security groups instead of private-plus-NAT.
2. **SQL Server Express** ($0 license): free-tier eligible and
   license-included-free forever; the 10 GB/database cap is far above this
   dataset.
3. **Single-AZ everything**: RDS single-AZ, single-instance broker,
   `desired_count = 1`. Availability is a paid feature this portfolio
   project doesn't need.

## Teardown strategy

The stack is built to be destroyed between demo sessions: no deletion
protection, no final snapshots, force-destroy on buckets and repositories.
Parked cost is ~$0 (S3/ECR pennies); `terraform apply` from scratch
restores everything except data — which the app reseeds.
