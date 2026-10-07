# Infrastructure Deployment

This directory contains reproducible Infrastructure-as-Code (Terraform) definitions for the portfolio architecture.

## Provider Split

- `cloudflare/`: Edge proxy, DNS, Cloudflare Turnstile bot protection, and Next.js frontend Worker bindings.
- `gcp/`: Google Cloud Run serverless host for `.NET 10` modular monolith (`Rafael.Portfolio.Web`), Artifact Registry container repository, Secret Manager, Workload Identity Federation, and dedicated service accounts.

## Directory Structure

```text
deployment/
├── cloudflare/
│   ├── main.tf                    # Turnstile widget, Worker script, routes, DNS
│   ├── variables.tf               # Parameterized inputs (account ID, domain, backend URL)
│   ├── outputs.tf                 # Turnstile site key, secret, and worker name
│   ├── versions.tf                # Provider requirements (cloudflare ~> 4.40)
│   ├── terraform.tfvars.example   # Example variables template (never commit real tokens)
│   └── scripts/
│       └── worker_placeholder.js  # Build scaffold placeholder
└── gcp/
    ├── main.tf                    # Cloud Run v2, Artifact Registry, Secret Manager, IAM
    ├── wif.tf                     # Workload Identity Pool, Provider, and CI deployer IAM
    ├── variables.tf               # Parameterized inputs (project, region, scaling, WIF)
    ├── outputs.tf                 # Cloud Run URI, repo ID, WIF provider name, SA emails
    ├── versions.tf                # Provider requirements (google ~> 6.0)
    └── terraform.tfvars.example   # Example variables template (never commit real credentials)
```

## Security and Cost Controls

1. **Scale-to-zero defaults**: Cloud Run `min_instances` defaults to `0` to prevent baseline idle costs.
2. **Bounded scaling**: `max_instances` defaults to `2` to mitigate cost drivers and denial-of-wallet spikes.
3. **Least privilege**:
   - Runtime Service Account (`sa-portfolio-backend`): Strictly granted `roles/secretmanager.secretAccessor` only on declared secrets.
   - CI Deployer Service Account (`sa-portfolio-ci`): Strictly granted `roles/artifactregistry.writer` on the backend repository, `roles/run.developer` on the Cloud Run service, and `roles/iam.serviceAccountUser` on the runtime account.
4. **Zero static keys (OIDC)**: Workload Identity Federation allows GitHub Actions to exchange short-lived tokens. No long-lived service account keys (`credentials_json`) are created or permitted.
5. **Scoped Trust Boundary**: The Workload Identity Provider strictly asserts:
   `assertion.repository == 'Riippex/Portfolio' && (assertion.ref == 'refs/heads/develop' || assertion.ref == 'refs/heads/main')`.
6. **Zero application persistence**: No database or durable storage is provisioned in this slice, maintaining the established zero-persistence baseline.
7. **Secret management**:
   - Cloudflare API tokens, Turnstile secret keys, and proxy identity HMAC secrets are kept in provider secret stores and GitHub environment secrets.
   - Example files (`*.tfvars.example`) contain non-sensitive placeholders.
   - Real `*.tfvars` and `*.tfstate` files are ignored by git.

## Local Validation

Verify Terraform definitions without mutating cloud infrastructure:

```bash
# Validate GCP definitions
cd deployment/gcp
terraform fmt -check
terraform init -backend=false
terraform validate

# Validate Cloudflare definitions
cd ../cloudflare
terraform fmt -check
terraform init -backend=false
terraform validate
```

> [!IMPORTANT]
> Authoring Terraform definitions does not imply permission to apply them. Only the project owner and Codex run commands that access live provider state or mutate cloud credentials.
