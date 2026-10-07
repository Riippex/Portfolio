# Infrastructure runbook

This document describes the reproducible infrastructure definitions for the portfolio, spanning Cloudflare and Google Cloud Platform (GCP).

## Provider Split and Ownership

- **Cloudflare**:
  - DNS apex and routing (`rafaelpatinodiaz.com`).
  - Next.js frontend Worker script execution (Vinext / Cloudflare Workers runtime).
  - Cloudflare Turnstile bot and abuse mitigation widget.
  - Edge identity header injection (`CF-Connecting-IP`).
- **Google Cloud Platform (GCP)**:
  - Google Cloud Run v2 container execution for the .NET 10 modular monolith (`Rafael.Portfolio.Web`).
  - Google Artifact Registry Docker repository (`portfolio`).
  - Google Secret Manager for sensitive runtime tokens (`turnstile-secret-key`, `proxy-identity-secret`, `contact-api-token`).
  - Dedicated runtime service account (`sa-portfolio-backend`) with least-privilege IAM bindings.

## Edge-to-Backend Security Boundary

1. **Signed Visitor Identity**:
   - The frontend Worker reads the verified client IP from Cloudflare's `CF-Connecting-IP` header.
   - It hashes and signs this identity using HMAC-SHA256 with `ASSISTANT_PROXY_IDENTITY_SECRET`.
   - The backend validates the signature in `X-Client-Key` / `X-Client-Key-Proof` before accepting requests in production.
2. **Turnstile Bot Mitigation**:
   - The public site key is exposed to the frontend browser widget via `NEXT_PUBLIC_TURNSTILE_SITE_KEY`.
   - The secret verification key is stored in GCP Secret Manager and injected securely into Cloud Run as `Turnstile__SecretKey`.
3. **Contact Delivery Credentials**:
   - Cloudflare Email API tokens and Account IDs remain in GCP Secret Manager (`Contact__ApiToken`, `Contact__AccountId`).
   - Defaults to `Contact:Enabled = false` until live activation is authorized by the owner.

## Cost and Resource Bounds

To prevent denial-of-wallet risks and unexpected cloud bills:

| Resource | Setting | Rationale |
|---|---|---|
| Cloud Run Min Instances | `0` | Scale-to-zero when idle eliminates baseline compute costs |
| Cloud Run Max Instances | `2` | Bounded capacity prevents runaway scaling during traffic bursts |
| Cloud Run CPU / Memory | `1 vCPU` / `512 MiB` | Minimal footprint sufficient for .NET 10 request handling |
| Artifact Registry | Docker format | Immutable digest references; cleanup policy can prune untagged layers |
| Data Persistence | None | Zero-storage architecture: no Cloud SQL, no Firestore database provisioned |

## CI/CD and Workload Identity Federation (OIDC)

Continuous deployment uses keyless OpenID Connect (OIDC) authentication between GitHub Actions and Google Cloud Platform. Long-lived service account key files (`.json`) are forbidden.

### OIDC Trust Configuration

1. **Workload Identity Pool**: `projects/{PROJECT_NUMBER}/locations/global/workloadIdentityPools/github-actions-pool`
2. **Provider**: `github-provider` with issuer `https://token.actions.githubusercontent.com`.
3. **Attribute Mapping**:
   - `google.subject` -> `assertion.sub`
   - `attribute.repository` -> `assertion.repository`
   - `attribute.ref` -> `assertion.ref`
4. **Trust Boundary Condition**:
   `assertion.repository == 'Riippex/Portfolio' && (assertion.ref == 'refs/heads/develop' || assertion.ref == 'refs/heads/main')`
   This prevents untrusted forks, pull requests, or unauthorized branches from impersonating the deployment identity.

### IAM Role Matrix

| Identity | Scope / Role | Purpose |
|---|---|---|
| `sa-portfolio-ci` | `roles/iam.workloadIdentityUser` | PrincipalSet bound to `Riippex/Portfolio` |
| `sa-portfolio-ci` | `roles/artifactregistry.writer` | Scoped strictly to `portfolio` repository for Docker push |
| `sa-portfolio-ci` | `roles/run.developer` | Scoped strictly to `rafael-portfolio-backend` Cloud Run service |
| `sa-portfolio-ci` | `roles/iam.serviceAccountUser` | Scoped strictly to `sa-portfolio-backend` runtime service account |
| `sa-portfolio-backend` | `roles/secretmanager.secretAccessor` | Runtime access to `turnstile-secret-key`, `proxy-identity-secret`, `contact-api-token` |

### GitHub Actions Secrets & Variables

- **Variables (`vars.*`)**:
  - `GCP_PROJECT_ID`: GCP project identifier.
  - `GCP_REGION`: Target region (default: `us-central1`).
  - `GCP_WORKLOAD_IDENTITY_PROVIDER`: Full provider URI from Terraform output.
  - `GCP_CI_SERVICE_ACCOUNT`: Email of `sa-portfolio-ci`.
  - `GCP_ARTIFACT_REPO`: Name of repository (`portfolio`).
  - `GCP_CLOUDRUN_SERVICE`: Cloud Run service name (`rafael-portfolio-backend`).
  - `CLOUDFLARE_ACCOUNT_ID`: Cloudflare account ID.
  - `PORTFOLIO_BACKEND_URL`: Public URL of Cloud Run backend for frontend binding.
  - `NEXT_PUBLIC_TURNSTILE_SITE_KEY`: Public Turnstile site key.
- **Secrets (`secrets.*`)**:
  - `CLOUDFLARE_API_TOKEN`: Cloudflare token with scoped Workers and DNS permissions.

## Verification Workflow

All infrastructure code must be validated locally before pull request or review handoff:

```bash
# 1. Format verification
terraform fmt -check deployment/gcp
terraform fmt -check deployment/cloudflare

# 2. Syntax and schema validation
terraform -chdir=deployment/gcp init -backend=false
terraform -chdir=deployment/gcp validate

terraform -chdir=deployment/cloudflare init -backend=false
terraform -chdir=deployment/cloudflare validate
```

## Activation Checklist (Owner and Codex Console)

Live application of infrastructure requires owner authorization:
- [ ] GCP project created and billing account attached.
- [ ] GCP authentication configured with least-privilege CI/CD identity.
- [ ] Container image built via `docker build -f backend/Dockerfile .` and pushed to Artifact Registry.
- [ ] Secret values populated in Secret Manager (`turnstile-secret-key`, `proxy-identity-secret`).
- [ ] Cloudflare API token generated with Zone, DNS, Workers, and Turnstile permissions.
- [ ] Custom domain DNS verified and Turnstile widget activated.
- [ ] GitHub repository variables (`vars.*`) and secrets (`secrets.*`) configured in repository settings.
