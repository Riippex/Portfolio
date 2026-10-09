# Infrastructure runbook

Reproducible infrastructure definitions for the portfolio across Cloudflare and Google Cloud Platform (GCP), the deployment workflow that releases onto them, and the order in which the owner brings a fresh project up.

Authoring or validating these definitions never applies them. Only the project owner and Codex run commands that touch live provider state, create credentials, or deploy. Every command in the bootstrap sections below is documentation for that console work; nothing in the repository runs it.

## Who owns what

Each piece has exactly one owner, so infrastructure maintenance cannot undo a release and a release cannot weaken security.

| Concern | Owner | Notes |
|---|---|---|
| GCP APIs, Artifact Registry, runtime and CI service accounts, Workload Identity Federation | Terraform (`deployment/gcp`) | Federation and impersonation APIs are declared in `apis.tf`. |
| Secret Manager **containers** | Terraform | Empty containers only. No secret value is ever part of Terraform. |
| Secret Manager **values** | Owner, out of band | `gcloud secrets versions add`, see below. |
| Cloud Run service shape: scaling, limits, ingress, runtime environment, secret references, IAM | Terraform | Created in bootstrap stage 3 (`create_service`). |
| Cloud Run **image** and deploy metadata | Deploy workflow | Terraform ignores exactly `template[0].containers[0].image`, `client`, `client_version`, and the deploy labels. |
| Frontend Worker **code** and plaintext variable `PORTFOLIO_BACKEND_URL` | Deploy workflow (`wrangler deploy`) | Not in Terraform, so an apply cannot replace released code. |
| Frontend Worker **secrets** `ASSISTANT_PROXY_IDENTITY_SECRET` and `TEAM_ALLOWLIST` | Owner, out of band | `wrangler secret put` on the exact Worker; `TEAM_ALLOWLIST` is separate for `dev` and `prod`. Deploys never touch secrets. |
| Frontend Worker stage `PORTFOLIO_STAGE`, rate-limit bindings, admission entry | `frontend/wrangler.jsonc` | Per Wrangler environment, baked into the build by `CLOUDFLARE_ENV` and checked offline. |
| Backend stage `Portfolio__Stage` | Terraform | Set from `environment` (`dev` or `prod`) on the Cloud Run service. |
| Shared model control database, its TTL policies and runtime access | Terraform, one stack (`deployment/control-ledger`) | Owned once for the whole portfolio and never by a stage; gated by `create_database`. See [model control](model-control.md). |
| Backend store settings `Assistant__ModelControl__Firestore__*` | Terraform (stage stack) | Project and database id of the shared database, both or neither. |
| Zone DNS record and Worker route | Terraform (`deployment/cloudflare`) | Off until `enable_custom_domain`. |
| Turnstile widget | Owner, in the Cloudflare console | The provider would store the widget secret in state, so Terraform does not manage it. |
| Contact (Cloudflare Email) credentials | Deferred | Contact is disabled; no credential is provisioned or read until a separate, owner-authorized activation. |

## Runtime safety of remote deployments

The backend on Cloud Run always runs with `ASPNETCORE_ENVIRONMENT=Production`. In that mode the host requires the signed visitor identity (`X-Portfolio-Identity` / `X-Portfolio-Identity-Proof`, see [Stage access and edge limits](#stage-access-and-edge-limits)) for the assistant, job matching, and contact endpoints, requires a Turnstile secret, and refuses to start without both secrets. `Development`, which accepts requests without a signed identity and bypasses Turnstile, remains a local-only mode and is never set by Terraform.

The deployment stage (`dev`, `staging`, `prod`) is a separate input, `environment`. It names and labels resources and chooses which branch the cloud identity trusts. It never selects the ASP.NET environment, so a "dev" deployment is exactly as strict as production. The backend serves only `dev` and `prod`; its stage is passed explicitly (see below).

## Stage access and edge limits

**One explicit stage.** The stage is a single server-side value that no request can influence and nothing infers from `NODE_ENV`, the hostname, or the ASP.NET environment. The Worker reads `PORTFOLIO_STAGE`, declared per Wrangler environment in `frontend/wrangler.jsonc` and baked into the build by `CLOUDFLARE_ENV`; the backend reads `Portfolio__Stage`, which Terraform sets from `environment`. Only `dev` and `prod` are deployed stages (`local` exists for workstation tooling and is refused by a deployed host). A missing or invalid stage fails closed: the Worker answers 503 and the backend refuses to start. `tools/check-wrangler-build.mjs` verifies the generated Worker configuration for each stage and the deploy workflow runs it before uploading.

**`dev` is private.** The Worker entry (`frontend/worker/entry.ts`) admits every request before Vinext serves anything, and `assets.run_worker_first` is `true` so static assets, cached responses, HTML, RSC payloads, optimised images and API routes all require an allowlisted visitor. Only the exact `GET` or `HEAD /health` stays public: the Worker answers it with `{"status":"ok"}` and the backend's `/health` is likewise content-free. The dev Cloud Run service has a public URL, so the backend also closes every other route, including OpenAPI, to callers without a signed identity: a team-tier visitor identity, or the Worker's short-lived service read (`GET` only, bound to its path, valid for five minutes) that the server-rendered pages use to load public data.

**Allowlist secret.** `TEAM_ALLOWLIST` is a Worker secret, separate for `dev` and `prod`, set with `wrangler secret put TEAM_ALLOWLIST --name rafael-portfolio-frontend-<stage>`. It is a JSON array of at most 64 exact IP strings, for example `["203.0.113.4","2001:db8::1"]`. Malformed entries, CIDR ranges, ports, wildcards, comma lists and non-strings invalidate the whole value; equivalent IPv6 spellings and an IPv4-mapped IPv6 address normalise to one canonical form. `dev` fails closed (503) when the value is missing, invalid or empty. In `prod` the list only gives its addresses the team limits: team access never bypasses Turnstile, input validation, the separate Contact limit or any other safety control. The value is never printed, logged or passed as a variable; the deploy job checks only that a secret with that name exists.

**Platform metadata only.** The visitor is the address in `CF-Connecting-IP` and the country from the runtime's `request.cf.country`, both asserted by Cloudflare. `X-Forwarded-For`, `Forwarded`, `X-Real-IP`, `True-Client-IP` and any caller-supplied tier, country or identity header are ignored, and a missing or invalid platform address is refused rather than replaced by another header. (These values are trustworthy because every request reaches the Worker through Cloudflare, on `workers.dev` and on the zone route alike.)

**Signed identity, version 2.** The Worker sends the backend `X-Portfolio-Identity`, the base64url of a JSON document with exactly `v`, `kind`, `ip`, `country`, `tier`, `stage`, `method` and `path`, and `X-Portfolio-Identity-Proof`, the lowercase hex HMAC-SHA256 over `portfolio-identity-v2\n` plus the identity header exactly as sent. The backend checks the proof in constant time before parsing, then validates a canonical IP, a two-character country, the tier, and that the stage, method and path match its own and the request. An identity therefore cannot be replayed against another stage or endpoint. The format and shared test vectors are in `docs/contracts/visitor-identity-v2.json`, which the frontend and backend suites both check.

**Edge limits.** Before dispatching to the backend, the Worker applies Cloudflare Workers Rate Limiting bindings to the shared assistant chat and job analysis quota (`/api/assistant/chat/stream`, `/api/jobs/analyze`): an ordinary address 5 per minute, an allowlisted team address 15 per minute, and 100 per minute per country, with team traffic still counting toward its country. A request refused for its own address does not spend country capacity. The per-address counter key is a stable, domain-separated HMAC pseudonym of the canonical address, bound to the stage but not to the endpoint (so chat and job analysis keep one quota and equivalent IPv6 spellings share one counter), keyed with the existing server-only `ASSISTANT_PROXY_IDENTITY_SECRET`; Cloudflare never receives the raw address, and the Worker answers 503 without counting when that secret is missing. The raw address stays request-local for admission, identity signing and Turnstile and is never logged. Refusals are 429 with `Retry-After: 60`; a missing or failing binding fails closed with 503. These counters are **approximate**: Cloudflare keeps them per location and updates them eventually, so a visitor reaching several locations, or a burst, can exceed the stated numbers briefly, and they are not a global quota. The backend keeps its own independent in-memory limits with the same numbers (per instance, with a bounded table), and Contact has its separate backend limit of 3 per 10 minutes.

**Rollout order.** A backend image that includes this change refuses to start without `Portfolio__Stage`, and the deploy job refuses a Worker without `TEAM_ALLOWLIST`. For an existing stage: set `TEAM_ALLOWLIST` on its Worker, apply `deployment/gcp` for the stage so the service carries `Portfolio__Stage` (a Terraform apply creates a new revision; it never replaces the deploy-owned image), and only then run the deploy workflow. The frontend and backend must be deployed together, because the version 1 colon-delimited identity is no longer accepted by the backend.

## Shared model control database

The Assistant's budget ledger needs exactly one Firestore database for the whole portfolio, so
dev and prod share one daily and monthly allowance and one set of permits. `deployment/control-ledger`
declares it, its TTL policies (counters expire 40 days after their period ends, and so does resolved
reservation metadata; unresolved reservations carry no expiry and are never deleted by TTL) and the least-privilege access of each stage's backend service account; it creates nothing
until `create_database = true`, which the owner sets deliberately, once. Stage stacks only pass
the project and database id to the service (`control_ledger_project_id`, `control_ledger_database_id`, both
or neither) and never declare a database, so a stage cannot create a second allowance. Order: apply the
stage stacks (to get their service account emails), apply `control-ledger` with those emails, set the two
stage variables to the same values, apply the stages, then follow the one-time initialization in
[model control](model-control.md). Paid model use stays disabled: no tariff is configured and the provider is
the disabled one. The ledger bounds model cost only; it is not a total invoice cap.

## Secrets and the data-handling policy

`docs/data-handling.md` forbids placing secret material in Terraform state, logs, URLs, or build output. `sensitive = true` only hides a value in output; it does not keep it out of state or plans. Therefore:

- Terraform declares no secret versions, no secret bindings, and no resource that computes a secret (the Cloudflare Turnstile widget resource does, so it is not used).
- **Provider credentials are never Terraform inputs.** A saved plan file records the value of every root variable, sensitive or not, so a token passed as a variable lands in the plan. The Cloudflare provider therefore has an empty configuration and authenticates from the `CLOUDFLARE_API_TOKEN` environment variable of the operator's shell (`export CLOUDFLARE_API_TOKEN=...` for the session, read from a secure store; never a `.tfvars` file, never committed). The Google provider uses application default credentials the same way. The offline plan proofs below show a synthetic token never reaches a saved plan.
- The deploy build receives only public values. The only secret the deploy workflow uses is `CLOUDFLARE_API_TOKEN`, and only in the Wrangler steps.

### One shared proxy identity secret

The Worker signs each visitor identity and the backend verifies it, so both sides must hold the same value. Generate it once, locally, and put it into both stores without writing it to a file, a shell history, a ticket, or a log:

```bash
# Backend (Secret Manager). Run after bootstrap stage 1 created the container.
openssl rand -base64 48 | tr -d '\n' | gcloud secrets versions add proxy-identity-secret --data-file=- --project <project-id>

# Frontend (the exact Worker). Run with the same value, for example by reading it
# from a secure store; wrangler secret put reads standard input.
npx wrangler secret put ASSISTANT_PROXY_IDENTITY_SECRET --name rafael-portfolio-frontend-<stage>
```

Rotate by adding a new Secret Manager version and re-running `wrangler secret put`, then redeploying so Cloud Run reads `latest`.

### Turnstile

The Turnstile widget is created and managed in the Cloudflare console. Allow the stage hostname (and `localhost` only for local work). Then:

- Put the **public site key** in the GitHub environment variable `NEXT_PUBLIC_TURNSTILE_SITE_KEY` (it is baked into the client bundle at build time and is public by design).
- Add the widget **secret key** to Secret Manager: `gcloud secrets versions add turnstile-secret-key --data-file=- --project <project-id>` (standard input).

## GCP bootstrap (staged, owner and Codex only)

A fresh project cannot be created in one apply: the service needs an image, the image needs the registry, and the service reads secret values at startup. Bootstrap is therefore staged, gated by `create_service` (default `false`).

**Stage 0: prerequisites.** Project exists with billing attached, and the Service Usage API is enabled (it is on by default). The operator has permission to enable APIs and manage IAM for the one-time apply.

**Stage 1: foundation.** `terraform -chdir=deployment/gcp apply -var-file=<stage>.tfvars` with `create_service = false` (the default, and the safe state for a fresh project). Keep one ignored variable file per stage (for example `dev.tfvars`; `*.tfvars` is git-ignored) and pass it to **every** plan and apply. Creates the required APIs (run, artifact registry, secret manager, iam, iamcredentials, sts, cloud resource manager), the Artifact Registry repository, the runtime and CI service accounts, Workload Identity Federation, and the **empty** secret containers `turnstile-secret-key` and `proxy-identity-secret` with their runtime-scoped accessor bindings. No service exists yet and no contact credential is created.

**Stage 2: out of band.** The owner (not Terraform, not CI):
1. Adds a version to each secret container (`gcloud secrets versions add ...`, previous section). Verify each has an enabled version: `gcloud secrets versions list <secret> --project <project-id>`. Terraform deliberately cannot check this, because reading a secret version would put its payload in state.
2. Authenticates Docker to the registry and pushes an initial image built from the repository root: `docker build -f backend/Dockerfile -t <region>-docker.pkg.dev/<project-id>/portfolio/backend:<immutable-tag> .`. Use an immutable tag or digest, never `latest`.

**Stage 3: service.** First **persist** the service-phase inputs: add both lines to the stage's ignored variable file, then review and apply with that same file.

```hcl
# dev.tfvars (ignored, kept for the life of the stage)
create_service  = true
container_image = "<region>-docker.pkg.dev/<project-id>/portfolio/backend:<immutable-tag>"
```

```bash
terraform -chdir=deployment/gcp plan  -var-file=dev.tfvars
terraform -chdir=deployment/gcp apply -var-file=dev.tfvars
```

Do not pass these two values as one-off command-line overrides: a later plan made without them would target the foundation phase (`create_service = false`) and silently schedule the service and its IAM for removal. This creates the Cloud Run service, the public invoker binding (callers still need the signed identity and Turnstile), and the CI deployer's service-scoped `roles/run.developer`. From here on `container_image` is only the creation image (Terraform ignores the image attribute, so later applies keep whatever the deploy workflow released), but it must stay set.

**Safeguards against losing an established service.** The service sets `lifecycle { prevent_destroy = true }` and `deletion_protection = true`. If a plan is ever made without the saved inputs, it fails closed instead of removing anything: `create_service = false` errors with `Instance cannot be destroyed`, and `create_service = true` without `container_image` errors with a precondition. A `terraform plan -destroy` is refused too. Retiring the service is a deliberate, reviewed change: remove both settings in a commit, apply that, and only then change `create_service`.

After a deploy, `terraform plan` should show no change to the image or deploy metadata. If Cloud Run records additional deploy-time attributes that Terraform now reports, extend the ignore list with the single attribute, never the whole service.

## Cloudflare Worker and route (staged)

1. **Widget and secrets first.** Create the Turnstile widget in the console (above) and create the Worker secrets `ASSISTANT_PROXY_IDENTITY_SECRET` and `TEAM_ALLOWLIST` for that stage (`dev` needs a valid, non-empty allowlist before it will serve anyone). `wrangler secret put ... --name rafael-portfolio-frontend-<stage>` creates the Worker as a draft if it does not exist.
2. **Deploy.** Run the deploy workflow. The frontend job builds with `CLOUDFLARE_ENV=<stage>`, which fixes the Worker name `rafael-portfolio-frontend-<stage>` in the generated config (a mismatched `--env` at deploy time is refused by Wrangler), requires the secret to exist on that exact Worker, then runs `wrangler deploy --env <stage> --var PORTFOLIO_BACKEND_URL:<url>`.
3. **Route.** Only after the Worker exists, apply `deployment/cloudflare` with `enable_custom_domain = true` and the zone ID. It creates the stage hostname record (`dev.<domain>` for dev, the apex for prod) and the route to the same Worker name.

`dev` is reachable on `workers.dev` until the route is enabled. `prod` has `workers_dev = false`, so it is served only through the Terraform-owned route.

Wrangler owns plaintext variables: a deploy replaces any plaintext variable set in the dashboard with the ones in the command, so manage `PORTFOLIO_BACKEND_URL` only through the GitHub environment variable. Secrets are never replaced or removed by a deploy.

## CI/CD

`ci.yml` validates every push and pull request and is also callable. `deploy.yml` is manual (`workflow_dispatch`) and fails closed:

```text
authorize  ->  validate (ci.yml at this exact commit)  ->  deploy-backend   (id-token: write)
                                                       ->  deploy-frontend  (no id-token)
```

1. **authorize** allows only `dev` from `refs/heads/develop` and `prod` from `refs/heads/main`. Any other pairing stops the run before validation or credentials.
2. **validate** calls `ci.yml` at the dispatched commit: backend build, unit, architecture, the published-backend integration smoke, formatting, frontend lint, tests and builds (including per-stage Wrangler dry runs), Terraform validation, the deployment invariant checks, and skill sync. Both deploy jobs need it and check out `github.sha`, so the artifact deployed is the commit that passed.
3. **deploy-backend** is the only job with `id-token: write`. It exchanges the OIDC token for the CI service account, pushes an image tagged with the commit SHA (no mutable tag), and updates the Cloud Run revision's image.
4. **deploy-frontend** holds only `contents: read`.

### Protected environments (owner setup in GitHub)

The workflow names an environment per target, but protection is configured in repository settings and is **not** in the repository. Create the `dev` and `prod` environments and set:

- **Required reviewers**: at least the owner for both, so every deployment waits for explicit approval.
- **Deployment branches**: `dev` limited to `develop`, `prod` limited to `main`.
- Environment-scoped values below, so a target cannot read the other's configuration.

| Kind | Name | Meaning |
|---|---|---|
| Variable | `GCP_PROJECT_ID`, `GCP_REGION`, `GCP_ARTIFACT_REPO`, `GCP_CLOUDRUN_SERVICE` | Target project, region, registry, service |
| Variable | `GCP_WORKLOAD_IDENTITY_PROVIDER`, `GCP_CI_SERVICE_ACCOUNT` | Terraform outputs `workload_identity_provider` and `ci_service_account_email` |
| Variable | `CLOUDFLARE_ACCOUNT_ID` | Cloudflare account |
| Variable | `PORTFOLIO_BACKEND_URL` | Cloud Run URL (`https`), written to the Worker as runtime configuration |
| Variable | `NEXT_PUBLIC_TURNSTILE_SITE_KEY` | Public site key from the console-managed widget |
| Secret | `CLOUDFLARE_API_TOKEN` | Workers deploy and secret-list permission only |

### Cloud trust

The Workload Identity provider accepts a token only when the repository, the GitHub environment, and the branch all match the stage: `assertion.repository == '<owner>/<repo>' && assertion.environment == '<stage>' && assertion.ref == '<trusted ref>'`, where dev and staging trust `refs/heads/develop` and prod trusts `refs/heads/main`. This repeats the workflow's pairing rule at the cloud boundary.

| Identity | Scope and role | Purpose |
|---|---|---|
| `sa-portfolio-ci` | `roles/iam.workloadIdentityUser` on itself, for the repository principal set | Keyless impersonation |
| `sa-portfolio-ci` | `roles/artifactregistry.writer` on the `portfolio` repository | Push images |
| `sa-portfolio-ci` | `roles/run.developer` on the backend service (stage 3) | Deploy revisions |
| `sa-portfolio-ci` | `roles/iam.serviceAccountUser` on `sa-portfolio-backend` | Act as the runtime account |
| `sa-portfolio-backend` | `roles/secretmanager.secretAccessor` on `turnstile-secret-key` and `proxy-identity-secret` | Runtime secret access |

No service account key is created or permitted, and the deployer cannot apply infrastructure.

## Cost and resource bounds

The container explicitly sets `resources.cpu_idle = true` for request-based
billing. When `resources` includes CPU/memory limits, omitting this setting can
select continuous CPU allocation and instance-based billing. Request-based
billing still charges instance startup/shutdown and active request processing;
scale-to-zero and budget alerts are not hard spending caps.

Both scaling scopes stay Terraform-owned: service-level `AUTOMATIC` scaling with
the configured minimum, and revision-level minimum/maximum limits. Explicit
service-level configuration absorbs API-returned defaults without ignoring
scaling changes. Confirm the effective billing/scaling settings and require a
fresh no-change plan after bootstrap or maintenance.

| Resource | Setting | Rationale |
|---|---|---|
| Cloud Run min / max instances | `0` / `2` | Scale to zero; bounded burst |
| Cloud Run CPU / memory | 1 vCPU / 512 MiB | Minimal footprint |
| Artifact Registry | Docker, SHA-tagged images | Immutable references; cleanup policy can prune old layers |
| Data persistence | None | No database or durable storage is provisioned |

## Offline verification

None of these touch a provider, a credential, or the network beyond provider plugin download for `init`:

```bash
terraform fmt -check -recursive deployment
terraform -chdir=deployment/gcp init -backend=false && terraform -chdir=deployment/gcp validate
terraform -chdir=deployment/cloudflare init -backend=false && terraform -chdir=deployment/cloudflare validate

node --test tools/check-deployment.test.mjs  # proves each invariant by breaking it
node tools/check-deployment.mjs         # checks the real definitions and workflows
node tools/check-terraform-plans.mjs    # offline plan proofs (needs both `init -backend=false` runs)

cd frontend
for stage in dev prod; do
  CLOUDFLARE_ENV=$stage npm run build:vinext
  node ../tools/check-wrangler-build.mjs $stage
  npx wrangler deploy --dry-run --env $stage --var PORTFOLIO_BACKEND_URL:https://backend.example.test
done
```

`check-terraform-plans.mjs` runs `terraform plan` only, with synthetic credentials, `-refresh=false`, a hand-built state, a closed proxy port so any provider call would fail loudly, and plan files kept outside the repository. It proves that (1) a synthetic Cloudflare token supplied through the environment appears nowhere in the saved plan (archive entries, JSON, text), while the same scanner does find a token passed as a root variable; and (2) for the real GCP stack, the fresh default creates no service, the persisted service-phase inputs keep an established service with no destroy, and losing `create_service`, `container_image`, or both, or planning a destroy, is refused.

`check-deployment.mjs` enforces, among others: a Production-only remote runtime; no secret payloads, credential inputs, or Worker script in Terraform; `prevent_destroy` and deletion protection on the service; the narrow image ignore list; the bootstrap gate; the federation APIs and dependencies; exactly the four deployer roles; `id-token: write` only on the backend job; and validation, branch pairing, and a single secret in the deploy workflow.

## Activation checklist (owner and Codex console)

- [ ] GCP project created, billing attached, Service Usage API enabled.
- [ ] Bootstrap stage 1 applied; outputs recorded.
- [ ] Shared proxy identity secret added to Secret Manager **and** to the target Worker with `wrangler secret put`.
- [ ] Turnstile widget created in the console; secret key in Secret Manager; site key in the GitHub variable.
- [ ] `TEAM_ALLOWLIST` set on each Worker (JSON array of at most 64 exact IPs; non-empty for `dev`), separately for `dev` and `prod`.
- [ ] Secret versions verified with `gcloud secrets versions list`; initial image pushed (immutable tag).
- [ ] Service-phase inputs saved in the stage's ignored variable file, then bootstrap stage 3 applied with that file (`create_service = true`).
- [ ] GitHub `dev` and `prod` environments created with required reviewers, deployment branches, variables, and `CLOUDFLARE_API_TOKEN`.
- [ ] First dispatch of the deploy workflow reviewed and approved.
- [ ] Cloudflare route applied after the Worker exists; DNS and Turnstile hostnames verified.
- [ ] Contact credentials and activation remain a separate, later decision.

## Known limits

- Offline checks validate configuration, not a target environment's live state. Record actual bootstrap and smoke results separately; never infer deployment from a successful test or plan. The real Worker secret-list gate, release metadata preservation, GitHub OIDC exchange and end-to-end identity/human verification require live checks.
- Two Terraform stacks cannot see each other's outputs; the backend URL is carried by the GitHub environment variable.
