# Slice 9 — Infrastructure as Code

**Date:** 2026-08-07
**Status:** Approved
**Slice:** 9 of the roadmap (phase 5, first half). Depends on 8 (done) so that what gets
deployed is already instrumented. Roadmap marks it **may split**; this spec keeps it whole
by leaving every pipeline concern (image pushes, SPA upload, blue-green, Lambda snapshot)
to slice 10.

## Why this slice exists

The README has claimed `infra/` (Terraform for ECS, Lambda, RDS, S3, IAM, VPC) and
`docs/cost-model.md` since day one; both sit in the deferred-claims register. Nothing about
the system is deployable today — there is no VPC, no registry to push an image to, no
database that isn't a local container. This slice makes the claim true: a Terraform
configuration that stands up the whole runtime surface from scratch, and the cost model
that proves a portfolio project can afford to run it.

**Success criterion (roadmap):** reproducible from scratch with `terraform apply`.
**Constraint found during design:** the local AWS SSO session is expired, so the live
apply rehearsal cannot run in this session. The slice ships apply-ready, `terraform
validate`-clean configuration; the live rehearsal is recorded in the ROADMAP as pending
credentials, in the same honest style as slice 6's rate-limited alert-fire leg.

## Decisions taken, with rejected alternatives

1. **One environment, one root, one real module.** A single `infra/` root config split
   into per-concern files (`network.tf`, `rds.tf`, `mq.tf`, `alb.tf`, `cloudfront.tf`,
   …), plus exactly one child module — `modules/ecs-service` — because it is instantiated
   twice (API, Alerts worker) and that reuse is real. *Rejected:* multi-environment
   workspaces or `envs/dev|prod` trees (there is one environment and no evidence a second
   is coming — YAGNI); module-per-concern decomposition (indirection with a single caller
   each, the classic Terraform over-abstraction).
2. **S3 backend with native lockfile, bootstrapped by a second tiny root.**
   `infra/bootstrap/` (local state) creates the versioned, encrypted state bucket;
   `infra/` then uses `backend "s3"` with `use_lockfile = true` (S3 conditional-write
   locking, Terraform ≥ 1.10 — the README's "state in S3 with locking" without the legacy
   table). *Rejected:* DynamoDB lock table (superseded by native locking; one more
   resource to explain); Terraform Cloud (external account dependency for a portfolio
   repo).
3. **No NAT gateway — ECS tasks run in public subnets with strict security groups; data
   stays private.** A NAT gateway is ~US$45/month sitting idle, the single biggest cost
   trap in the classic three-tier VPC, and it buys outbound-only posture for tasks that
   already terminate no inbound traffic except from the ALB security group. Tasks get
   public IPs for image pulls and the Yahoo feed; RDS and Amazon MQ live in private
   subnets reachable only from the task security groups. The trade-off is recorded in
   ADR-011 and flagged for slice 11 to revisit. *Rejected:* NAT gateway (cost); VPC
   interface endpoints for ECR/Secrets Manager/CloudWatch (four-plus endpoints at ~US$8
   each per month exceeds the NAT they replace at this scale).
4. **One CloudFront distribution fronts everything.** Default behavior → S3 (SPA, Origin
   Access Control, bucket fully private); `/api/*` and `/hubs/*` behaviors → the ALB
   origin (caching disabled, all headers/cookies forwarded; CloudFront speaks WebSockets,
   so SignalR works). The SPA and the API share one origin, so the auth cookie needs no
   SameSite=None and there is no CORS story to get wrong — ADR-003's cookie decision
   carries over unchanged. The ALB itself is HTTP :80 only this slice: with no custom
   domain there is nothing an ACM certificate can attest, and an HTTPS SPA calling an
   HTTP API cross-origin would be blocked as mixed content — same-origin CloudFront is
   what makes the no-domain setup work at all. Viewer-facing TLS is CloudFront's default
   certificate; origin-side TLS and a custom domain are slice 11 hardening. *Rejected:*
   separate API subdomain + ACM (requires owning a domain, which the project doesn't);
   exposing the ALB directly (mixed content, CORS, cookie flags all reopen).
5. **RDS SQL Server Express on db.t3.micro, credentials owned by RDS-managed Secrets
   Manager.** Express edition is license-included-free and free-tier eligible (750
   h/month, 20 GB, 12 months); its 10 GB-per-database cap is far above this dataset.
   `manage_master_user_password = true` puts the master secret in Secrets Manager with no
   password ever in state or code; task definitions read it via ECS `secrets` +
   `ConnectionStrings__MarketPulse` composition in the container entrypoint. Single-AZ,
   `skip_final_snapshot = true`, deletion protection off — this stack is built to be torn
   down. *Rejected:* Web/Standard editions (license cost, no free tier); SQL Server in a
   Fargate task (databases don't belong on ephemeral compute, and it forfeits the RDS
   line the README promises).
6. **Amazon MQ for RabbitMQ, mq.t3.micro single-instance — added to the slice even though
   the roadmap's resource list omits it.** The roadmap names VPC/RDS/ECS/ALB/ECR/
   CloudFront+S3, but the alerts pipeline is nonfunctional without a broker; omitting it
   would produce infrastructure that cannot run the system, failing the slice's own
   success criterion. mq.t3.micro is free-tier eligible (750 h/month, 12 months). The
   omission and its correction are recorded in ADR-011. **Known follow-up owned by slice
   10:** Amazon MQ speaks AMQPS (5671) only, and `RabbitMqOptions` has no TLS switch —
   the app-side `Ssl` option lands with the deployment wiring, not here. *Rejected:*
   RabbitMQ as an ECS service (no acceptable persistence/clustering story on Fargate);
   migrating to SQS/SNS (a protocol rewrite of the messaging layer is not an IaC slice).
7. **ECR repositories for both images, and the Alerts worker finally gets a Dockerfile.**
   Only `MarketPulse.Api` has one today; an ECS service for the worker with no buildable
   image would be scaffolding for nothing. The Alerts Dockerfile mirrors the API's
   multi-stage shape. ECS services are created pointing at `:latest` with
   `wait_for_steady_state` off, so the first `terraform apply` succeeds against empty
   repositories and tasks start once slice 10's pipeline pushes images — the bootstrap
   order (`apply` → push → tasks start) is documented in `infra/README.md`.
8. **CI gets an `infra` job: `terraform fmt -check` + `terraform validate` on both
   roots**, with `terraform init -backend=false` so no credentials are needed. This is
   the same "prove it compiles" bar the other jobs hold; `terraform plan` in CI needs
   cloud credentials and is slice 10's concern. *Rejected:* tflint/checkov (not
   installed, new toolchain to justify later, not blocking the criterion).
9. **Region `ap-southeast-2`, everything tagged.** The app watches the ASX; data
   residency follows the data. `default_tags` on the provider (`Project=MarketPulse`,
   `ManagedBy=Terraform`) so every resource is attributable in Cost Explorer.

## Scope boundary

### In scope

- `infra/bootstrap/` — state bucket root (local state, applied once).
- `infra/` — VPC (2 AZs, public + private subnets, no NAT), security groups, ALB +
  target groups + health checks (`/health` liveness per slice 8's split), ECS cluster +
  two `ecs-service` module instances (API on 8080 behind the ALB; Alerts worker,
  no load balancer), ECR ×2, RDS SQL Server Express, Amazon MQ for RabbitMQ, S3 SPA
  bucket (private) + CloudFront (OAC, dual-origin, `/api/*` + `/hubs/*` behaviors,
  SPA 403/404 → `/index.html` fallback), task/execution IAM roles (least privilege:
  secrets read scoped to the two secrets, no wildcard resources), CloudWatch log groups
  (14-day retention), task-definition `secrets` wiring for DB and MQ credentials,
  `Otel__OtlpEndpoint` left unset (no collector in the cloud yet — slice 10 decides the
  exporter story; the app already tolerates an absent collector locally).
- `src/MarketPulse.Alerts/Dockerfile` + a second build in the CI `docker` job.
- `infra/README.md` — bootstrap order, variables, teardown, what slice 10 adds.
- `docs/cost-model.md` — per-resource monthly table (free-tier and post-free-tier
  columns), the three deliberate cost decisions (no NAT, Express, single-AZ), teardown
  strategy, resolving the deferred-claims row.
- ADR-011 — the IaC shape: decisions 3, 4, 5, 6 above with their rejected alternatives.
- CI `infra` job (fmt + validate, no credentials).
- README (repo tree `infra/` note, deferred-claims corrections) and ROADMAP updates
  (slice 9 row → done with the honest apply-rehearsal caveat; broker omission noted).

### Out of scope

- Everything slice 10 owns: image build/push to ECR, SPA build/upload to S3, the deploy
  workflow, ECS blue-green (CodeDeploy), the Lambda EOD snapshot (the roadmap places
  Lambda in slice 10 despite the README tree comment), `terraform plan/apply` in CI, and
  the app-side `RabbitMqOptions.Ssl` change with Amazon MQ wiring.
- Slice 11 hardening: custom domain + ACM, origin-side TLS ALB→tasks, WAF, revisiting
  the no-NAT posture, secrets rotation.
- Datadog/CloudWatch OTel exporters, dashboards-as-code (deferred from slice 8, still
  deferred).
- Multi-environment layout, Route 53, autoscaling policies (desired_count is a variable,
  default 1).

## Testing

`terraform fmt -check` and `terraform validate` clean on both roots, locally and as the
new CI job; the Alerts Dockerfile must build in CI alongside the API's. The live
`terraform apply` rehearsal (the slice's real acceptance test) is blocked on refreshed
AWS credentials and recorded as pending in the ROADMAP — the configuration ships
apply-ready, and the rehearsal note names what to verify when it runs: clean apply from
empty account, CloudFront URL serving the SPA fallback, `/health` 200 through the edge
once images exist, clean `terraform destroy`.
