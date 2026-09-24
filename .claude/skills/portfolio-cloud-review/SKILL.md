---
name: portfolio-cloud-review
description: Plan or review Portfolio Cloudflare and Google Cloud infrastructure, IAM, deployment, observability, security, and cost changes. Do not deploy, change billing, DNS, secrets, or cloud resources without explicit authorization.
---

# Portfolio cloud review

Build an evidence-backed view of the proposed infrastructure change.

## Establish ownership

Read `docs/architecture.md`, `deployment/`, affected application configuration, and accepted ADRs. Confirm current provider documentation, supported versions, regional availability, and prices when they can change. Distinguish configuration present in the repository from resources that actually exist.

The intended split is:

- Cloudflare: DNS, frontend Worker, edge routing, Turnstile, and edge protections.
- Google Cloud: Cloud Run backend, Vertex AI or approved model access, Firestore/vector storage when introduced, Cloud Storage, Secret Manager, and service observability.

## Review

- Map every resource to its runtime, environment, data classification, CI identity, and owner.
- Keep IAM identities separate and least-privilege; scope OIDC trust, service invocation, secret access, and deploy permissions to concrete repositories, branches, environments, and resources.
- Check scale-to-zero, concurrency, timeout, retry, cold-start, quota, egress, logging, retention, and model-token cost implications.
- Confirm secrets stay in provider secret stores and never enter frontend bundles, repository variables, logs, build artifacts, or Terraform state in plaintext.
- Treat Cloudflare-to-Cloud-Run authentication, CORS, abuse prevention, and streaming behavior as an end-to-end boundary.
- Include migration, rollback, health, and smoke-check strategy. A successful plan or build is not a deployment.

Report assumptions, monthly or per-request cost drivers, security and availability risks, recommended limits, validation performed, and actions requiring owner approval. Never create resources merely to verify a plan.
