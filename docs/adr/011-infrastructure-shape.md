# ADR-011: Infrastructure shape — one environment, no NAT, one distribution

**Status:** Accepted · **Date:** 2026-08-07

## Context

Slice 9 makes the README's oldest claims true: `infra/` (Terraform) and
`docs/cost-model.md` have been promised since day one. The constraints shaping every
decision below are the same two that shaped the fake-feed and observability slices: this is
a portfolio project that must be **cheap enough to actually run** (free tier where
possible, torn down between demo sessions) and **honest about what it is** (one
environment, no invented high-availability story). The roadmap's success criterion is
"reproducible from scratch with `terraform apply`".

One constraint arrived during design rather than before it: the local AWS SSO session is
expired, so the live apply rehearsal could not run in the building session. The
configuration ships `fmt`/`validate`-clean and CI-gated; the rehearsal is recorded as
pending in the roadmap rather than claimed.

## Decision

1. **State in S3 with native locking, bootstrapped by a second tiny root.**
   `infra/bootstrap/` (local state, applied once) creates a versioned, encrypted state
   bucket; `infra/` uses `backend "s3"` with `use_lockfile = true` — S3 conditional-write
   locking, available since Terraform 1.10. *Rejected:* a DynamoDB lock table (the
   pre-1.10 pattern; one more resource whose only job is being explained) and Terraform
   Cloud (an external account dependency for a repo meant to be cloneable).

2. **No NAT gateway. ECS tasks run in public subnets with strict security groups; data
   stays private.** A NAT gateway is ~US$45/month idle — the single biggest cost trap in
   the classic three-tier VPC — and what it buys here is outbound-only posture for tasks
   that already accept no inbound traffic except the ALB's security group (and the ALB
   itself accepts port 80 only from CloudFront's origin-facing managed prefix list). Tasks
   need outbound reach anyway: ECR pulls, Secrets Manager, CloudWatch, and the Yahoo feed.
   RDS and Amazon MQ sit in private subnets with no internet route in either direction,
   reachable only from the task security groups. *Rejected:* the NAT gateway (cost without
   a threat it mitigates at this scale) and VPC interface endpoints (ECR needs two, plus
   Secrets Manager, plus CloudWatch Logs — four-plus endpoints at ~US$8/month each exceeds
   the NAT they replace). Slice 11's hardening pass owns revisiting this posture.

3. **One CloudFront distribution fronts both the SPA and the API.** Default behavior →
   private S3 bucket via Origin Access Control; `/api/*` and `/hubs/*` → the ALB origin
   with caching disabled and all viewer headers/cookies forwarded (CloudFront passes
   WebSocket upgrades, so SignalR works). Same-origin is the load-bearing property: the
   auth cookie keeps ADR-003's posture with no SameSite=None, there is no CORS
   configuration to get wrong, and — decisive — no mixed-content problem, because an HTTPS
   page cannot call an HTTP API cross-origin, and without owning a domain there is no ACM
   certificate to put on the ALB. The ALB is therefore HTTP-only this slice; viewer-facing
   TLS is CloudFront's default certificate. The SPA's route fallback is a CloudFront
   Function rewriting extension-less paths to `/index.html` on the S3 behavior only — a
   distribution-wide `custom_error_response` would also rewrite the API's 403s into
   200-`index.html`, breaking real auth failures. *Rejected:* a separate API subdomain
   with ACM (requires a domain the project doesn't own) and exposing the ALB directly
   (mixed content, CORS, and cookie flags all reopen at once). Custom domain, origin-side
   TLS, and WAF are slice 11.

4. **RDS SQL Server Express on db.t3.micro, master password owned by RDS-managed Secrets
   Manager.** Express is license-included-free and free-tier eligible; its 10 GB
   per-database cap is far above this dataset. `manage_master_user_password = true` means
   no password in code, in `tfvars`, or composed in state's plain environment; task
   definitions inject `DB_USERNAME`/`DB_PASSWORD` from the secret and each container's
   entrypoint composes `ConnectionStrings__MarketPulse` at start-up. Single-AZ, no
   backups, no deletion protection, `skip_final_snapshot` — the stack is built to be torn
   down (see the cost model's teardown strategy). *Rejected:* Web/Standard editions
   (license cost, no free tier) and SQL Server in a Fargate task (a database on ephemeral
   compute, and it forfeits the RDS line the README promises).

5. **Amazon MQ for RabbitMQ, mq.t3.micro single-instance — added although the roadmap's
   resource list omitted it.** The roadmap named VPC/RDS/ECS/ALB/ECR/CloudFront+S3; no
   broker. But the alerts pipeline is nonfunctional without one, and infrastructure that
   cannot run the system fails the slice's own success criterion — so the broker ships,
   and the omission is recorded here rather than papered over. mq.t3.micro is free-tier
   eligible. Amazon MQ speaks AMQPS (5671) only, and `RabbitMqOptions` has no TLS switch
   today: that app-side change belongs to slice 10's deployment wiring and is named in the
   roadmap, not silently absorbed. *Rejected:* RabbitMQ as an ECS service (no acceptable
   persistence story on Fargate) and migrating to SQS/SNS (a protocol rewrite of the
   messaging layer is not an IaC slice).

## Consequences

- **The public-subnet posture is a real trade-off**, not a free lunch: task ENIs carry
  public IPs, and the blast radius of a security-group mistake is larger than it would be
  behind a NAT. The mitigations are that no task SG admits any inbound flow except the
  ALB→API rule, and that slice 11 explicitly owns re-examining this.
- **The first `terraform apply` succeeds against empty ECR repositories** — both services
  are created with `wait_for_steady_state` off and sit at zero running tasks until slice
  10's pipeline pushes images. "Applied" and "running" are deliberately different claims.
- **The live apply rehearsal is pending credentials.** Until it runs (clean apply from an
  empty account, CloudFront URL serving the SPA fallback, clean destroy), "reproducible
  from scratch" is asserted by construction and CI validation, not demonstrated — the
  roadmap records this in the same style as slice 6's rate-limited alert-fire leg.
- Slice 10 inherits three named seams: image push + SPA upload + service flip, the
  `RabbitMqOptions` TLS switch for AMQPS, and the cloud OTel exporter decision
  (`Otel__OtlpEndpoint` is deliberately unset in the task definitions).
