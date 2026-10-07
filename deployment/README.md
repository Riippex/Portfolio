# Infrastructure Deployment

Reproducible Infrastructure-as-Code (Terraform) for the portfolio architecture. The operating model, bootstrap order, and activation checklist are in [docs/runbooks/infrastructure.md](../docs/runbooks/infrastructure.md); this file is the map.

## Provider Split

- `cloudflare/`: zone routing for the frontend only: the stage hostname DNS record and the route to the deployed Worker. It does not manage the Worker script, its variables or secrets, or the Turnstile widget.
- `gcp/`: the Google Cloud Run host for the `.NET 10` modular monolith (`Rafael.Portfolio.Web`), Artifact Registry, empty Secret Manager containers, the required service APIs, Workload Identity Federation, and the runtime and CI service accounts.

## Directory Structure

```text
deployment/
├── cloudflare/
│   ├── main.tf                    # Stage hostname DNS record and Worker route
│   ├── variables.tf               # Stage, Worker name prefix, zone (no secrets)
│   ├── outputs.tf                 # Worker name and hostname
│   ├── versions.tf                # Provider requirements (cloudflare ~> 4.40)
│   └── terraform.tfvars.example   # Example variables template (never commit real tokens)
└── gcp/
    ├── apis.tf                    # Required service APIs, including federation APIs
    ├── main.tf                    # Registry, runtime account, secret containers, Cloud Run (gated)
    ├── wif.tf                     # Workload Identity Pool, Provider, and CI deployer IAM
    ├── variables.tf               # Inputs, including the create_service bootstrap gate
    ├── outputs.tf                 # Service URI, repository, WIF provider, service accounts
    ├── versions.tf                # Provider requirements (google ~> 6.0)
    └── terraform.tfvars.example   # Example variables template (never commit real credentials)
```

Outside this directory: `frontend/wrangler.jsonc` (per-stage Worker names), `.github/workflows/` (CI and the gated manual deploy), and `tools/check-deployment.mjs` with `tools/check-wrangler-build.mjs` (offline regression checks).

## Ownership

| Terraform owns | The deploy workflow owns | The owner owns, out of band |
|---|---|---|
| APIs, registry, identities, federation, secret containers, Cloud Run shape (scaling, limits, ingress, environment, secret references, IAM), zone route and DNS | Cloud Run image (SHA-tagged), Worker code, `PORTFOLIO_BACKEND_URL` | Every secret value, the Turnstile widget, GitHub environment protection |

Terraform ignores only the deploy-owned Cloud Run attributes and never declares the Worker script, so infrastructure maintenance cannot replace a release.

## Security and Cost Controls

1. **Production-grade remote runtime**: Cloud Run always runs `ASPNETCORE_ENVIRONMENT=Production`, which requires the signed proxy identity and Turnstile. The stage name (`environment`) never selects it.
2. **No secrets in Terraform**: containers only. Values are added with `gcloud secrets versions add` and `wrangler secret put`.
3. **Staged bootstrap**: `create_service = false` first; the service and its service-scoped IAM only after secret versions and an image exist.
4. **Scale-to-zero, bounded scaling**: `min_instances = 0`, `max_instances = 2`.
5. **Least privilege, keyless**: the CI account holds exactly four resource-scoped roles, reached through Workload Identity Federation that trusts one repository, GitHub environment, and branch per stage. No service account keys.
6. **Gated deployment**: manual only; authorize (target and branch), then validate the exact commit (including the published-backend integration smoke), then deploy. Only the backend job requests `id-token: write`.
7. **Zero application persistence**: no database or durable storage.
8. **Ignored local state**: real `*.tfvars` and `*.tfstate` are git-ignored; example files hold placeholders.

## Local Validation

Verify the definitions without touching any provider:

```bash
terraform fmt -check -recursive deployment
terraform -chdir=deployment/gcp init -backend=false && terraform -chdir=deployment/gcp validate
terraform -chdir=deployment/cloudflare init -backend=false && terraform -chdir=deployment/cloudflare validate

node --test tools/check-deployment.test.mjs
node tools/check-deployment.mjs
```

> [!IMPORTANT]
> Authoring Terraform definitions does not imply permission to apply them. Only the project owner and Codex run commands that access live provider state, mutate cloud credentials, or deploy.
