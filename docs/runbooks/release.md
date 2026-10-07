# Release runbook

This document defines the release gates, the pre-flight checklist, the privacy and data classification audit, the Contact activation boundary, the cost estimate and spend controls, what the offline checks do and do not establish, the smoke verification, and the owner-only promotion procedure for releasing the portfolio.

Nothing here activates a deployment, a cloud resource, a mailbox, or email delivery. Those steps are owner decisions, described in [the infrastructure runbook](infrastructure.md) and [the Contact runbook](contact.md).

## Release Governance and Boundary

- **Integration vs Release**: `develop` is the active integration branch. `main` is the strict release boundary.
- **Authorization Gate**: Promoting `develop` to `main`, publishing a git release, or deploying production infrastructure requires explicit owner authorization. No agent or automated tool may merge into `main` or update `main` autonomously.
- **Rollback**: Cloud Run and Cloudflare Workers both keep previous revisions, so a regression is answered by switching traffic or re-deploying the previous known-good revision (see the rollback procedure).

## Pre-flight Checklist

Before initiating a release to production:

- [ ] Working tree is clean (`git status` reports no untracked or modified files).
- [ ] CI is green for the **exact commit** to be promoted (GitHub Actions, workflow `CI`). It runs on Linux and covers the backend unit, architecture, and published-backend integration tests, formatting, the frontend lint, tests and both builds including per-stage Wrangler dry runs, and the infrastructure and release checks below.
- [ ] Infrastructure and release checks pass (they also run in CI):
  - `node --test tools/check-deployment.test.mjs` and `node tools/check-deployment.mjs`
  - `node tools/check-terraform-plans.mjs` (needs both `terraform init -backend=false` runs)
  - `node --test tools/check-release.test.mjs` and `node tools/check-release.mjs`
- [ ] Agent skills are synchronized: `pwsh tools/sync-agent-skills.ps1 -Check` passes.
- [ ] Contact stays **disabled** in this release unless the owner has completed the activation prerequisites in the [Contact runbook](contact.md): provider and mailbox retention reviewed, mailbox cleanup defined and verified, sender, destination and credential set up, and one authorized live smoke send observed. Offline readiness is not activation.
- [ ] No private files (`documents/`, `.env`, machine configs) or raw credentials are staged or committed.
- [ ] The owner has read [What the Offline Checks Establish](#what-the-offline-checks-establish) and accepts the live items it lists as unverified.

## Privacy & Data Classification Audit

The portfolio follows the [Data handling policy](../data-handling.md). The table separates what the **application** does from what happens **outside the application**, because the application cannot make claims about systems it does not control.

| Data class | Capability | Application handling | Outside the application |
|---|---|---|---|
| **Public evidence** | Biography, case studies, citations | Versioned public files in `docs/evidence/`, bundled into the container at publish time. | Git hosting and the container registry hold copies. |
| **Durable operational records** | None designed | No database or durable disk is provisioned by this repository's Terraform, and the code declares no persistence layer. This is a design property checked by review and tests; the automated checks are tripwires, not proof. | Cloud and edge platforms keep their own operational logs and metadata for requests (for example Cloud Run request logs under Cloud Logging's retention). |
| **Session-transient input** | Chat turns, vacancy text, contact messages | Processed in memory for the duration of the request and then dropped. | Transit through Cloudflare and Google Cloud infrastructure. |
| **Short-lived control state** | Rate-limit buckets, Turnstile outcomes | In-memory sliding windows keyed by pseudonymous identifiers, with TTL eviction; lost on restart. | Turnstile verification is performed by Cloudflare. |
| **Sensitive ephemeral material** | Visitor name, email address, message body | Validated and bounded (64 KiB request body, 5,000-character message) and relayed. Application logs carry fixed outcome codes, status, latency, and a pseudonymous client hash (by code review of `ContactEndpoints.cs`; not asserted by an automated test). | When Contact is activated, Cloudflare Email Service processes the message and the recipient mailbox keeps it until the owner deletes it. Neither is under the application's control and neither is zero-retention. See the [Contact runbook](contact.md). |
| **Secrets & Credentials** | Turnstile secret, proxy identity secret | Two managed Secret Manager secrets, `turnstile-secret-key` and `proxy-identity-secret`, plus the Worker secret `ASSISTANT_PROXY_IDENTITY_SECRET`, all populated out of band. No contact credential exists while Contact is disabled. Values are not in git or Terraform by design. | The secret stores themselves. |

### Design invariants and how each is checked

| Invariant | Checked by | Not checked by |
|---|---|---|
| Contact is relay-only: no receipts or storage of messages | `ContactEndpointTests` (real Kestrel listener with a fake provider transport), `CloudflareEmailRelayBoundsTests`, code review | No test proves the absence of a storage path; the persistence tripwires only catch known APIs. |
| The assistant keeps no chat history or profile across requests | `AssistantServiceTests`, `AssistantStreamTests`, code review | The same limit as above. |
| Job matching keeps vacancy text only for the request | `JobMatchingServiceTests`, code review | The same limit as above. |
| In production all interactive endpoints require the signed edge identity | `AssistantClientIdentityTests`, the published-backend integration smoke (a Production host rejects unsigned and forged requests), the Terraform setting checked by `check-release.mjs` | Real traffic through a deployed Worker and Cloud Run. |

## Contact Activation Boundary

- **Contact is disabled.** Terraform sets `Contact__Enabled=false` on the service, the endpoint answers 503, and no contact credential is provisioned or read at startup. A release does not activate Contact.
- **Activation is owner-authorized.** It needs the prerequisites in the [Contact runbook](contact.md): a Cloudflare-managed sender domain, a verified destination, a scoped API token, a reviewed provider and mailbox retention position, a mailbox cleanup process the owner defines and verifies, and one authorized live smoke send recorded separately from automated tests.
- **Retention is scoped.** The application stores nothing. Cloudflare's processing and the recipient mailbox retain data under their own policies and are not purged by the application.
- **Offline is not live.** Fake-provider tests show the relay behaves correctly against the documented response shape. They do not show that a real message was sent, accepted, or delivered.

## Cost Estimate & Spend Controls

**Retrieved: 2026-10-07.** These are estimates built from published unit prices and the assumptions below. They are not quotes and not limits. Confirm the numbers in the Google Cloud pricing calculator and the Cloudflare dashboard before relying on them.

Sources:

- Cloud Run: https://cloud.google.com/run/pricing
- Secret Manager: https://cloud.google.com/secret-manager/pricing
- Artifact Registry: https://cloud.google.com/artifact-registry/pricing
- Cloudflare Workers: https://developers.cloudflare.com/workers/platform/pricing/

Provenance: the Cloudflare page was fetched directly (it states it was last updated 2026-10-02). The Google pricing pages render with JavaScript and could not be read directly, so their figures were taken from search extraction of those official pages on the retrieval date and should be re-confirmed. Cloud Logging and egress figures come from the same extraction (Cloud Logging pricing, Network Service Tiers pricing).

### Unit prices used

| Item | Price or allowance |
|---|---|
| Cloud Run, request-based billing, us-central1 | $0.000024 per vCPU-second, $0.0000025 per GiB-second, $0.40 per million requests. Billed while handling requests **and during instance startup and shutdown**; nothing while idle at `min_instances = 0`. |
| Cloud Run request-based free tier (monthly) | 180,000 vCPU-seconds, 360,000 GiB-seconds, and 2 million requests, aggregated per billing account. |
| Secret Manager | 6 active secret versions and 10,000 access operations free per month; about $0.06 per active version per month and $0.03 per 10,000 operations beyond that. |
| Artifact Registry | First 0.5 GiB free; about $0.10 per GiB-month beyond (reported as $0.000136986 per GiB-hour). |
| Internet egress from Cloud Run (Premium tier, North America) | First 1 GiB free per month, then $0.12 per GiB up to 1,024 GiB. |
| Cloud Logging | First 50 GiB ingested per project per month free, then $0.50 per GiB (30 days of storage included). |
| Cloudflare Workers Free | 100,000 requests per day and **10 ms CPU per invocation**. Static asset requests are free and unlimited. |
| Cloudflare Workers Paid | $5 per month per account minimum, including 10 million requests and 30 million CPU milliseconds; $0.30 per extra million requests and $0.02 per extra million CPU milliseconds. |

### Assumptions

- Region `us-central1`, request-based billing, `min_instances = 0`, `max_instances = 2`, 1 vCPU, 512 MiB (0.5 GiB).
- Each backend request bills 0.2 s of CPU and memory (pessimistic: no overlap between concurrent requests). Each cold start bills 4 s. Average response 20 KiB. About 1 KiB of logs per request.
- Backend requests per month: **light** 10,000 with 500 cold starts; **moderate** 100,000 with 2,000; **stress** 3,000,000 with 20,000.
- Registry: each release adds about 60 MiB of new layers, 30 releases retained, no cleanup policy.
- Secrets: the two managed secrets with one active version each, read twice per instance start.
- Not included: domain registration, taxes, and other usage on the same billing account that shares the free tiers.
- **Contact (optional, disabled):** Cloudflare Email Service costs are not estimated here. The service is documented as beta, and its pricing and the account's entitlement must be confirmed at https://developers.cloudflare.com/email-service/platform/pricing/ before activation. The Contact runbook lists the prerequisites.

### Estimated monthly cost (USD)

| Component | Light | Moderate | Stress | Working |
|---|---|---|---|---|
| Cloud Run compute and requests | $0.00 | $0.00 | $12.40 | Stress: 680,000 vCPU-s, so (680,000 − 180,000) × $0.000024 = $12.00; 340,000 GiB-s is inside the free tier; (3M − 2M) × $0.40 per million = $0.40. |
| Egress | $0.00 | $0.11 | $6.75 | 0.19, 1.91, and 57.2 GiB; $0.12 per GiB beyond the first. |
| Artifact Registry | $0.13 | $0.13 | $0.13 | 30 × 60 MiB = 1.8 GiB; (1.8 − 0.5) × $0.10. |
| Secret Manager | $0.00 | $0.00 | $0.09 | 2 versions are free; 40,000 reads at stress, so 3 × $0.03. |
| Cloud Logging | $0.00 | $0.00 | $0.00 | All scenarios stay far below 50 GiB. |
| **Google Cloud subtotal** | **about $0.13** | **about $0.24** | **about $19.37** | Sum of the lines above, each rounded to the cent. |
| Cloudflare Workers | $0 or $5 | $0 or $5 | $5 | See the uncertainty below. |
| **Total** | **$0.13 to $5.13** | **$0.24 to $5.24** | **about $24.37** | |

**Worker plan uncertainty.** The Free plan allows 10 ms of CPU per invocation. Whether server-rendered Next.js routes on Vinext stay under that has **not been measured**. If they do not, the Paid plan's $5 monthly minimum applies. Measure CPU time per route (Workers metrics or `wrangler tail`) before choosing a plan. Stress traffic (about 100,000 per day) is at the Free daily request limit regardless.

**Registry growth.** Terraform sets no cleanup policy, so images accumulate; the figure above grows with releases (about $0.05 more per extra 0.5 GiB).

**Secret versions.** Rotation adds versions. Disable or destroy old ones; more than 6 active versions per billing account start costing.

**Illustrative instance-time ceiling.** If both instances were busy for a whole 30-day month, instance time alone would bill about $125.68 at the rates above (5,004,000 billable vCPU-seconds × $0.000024 plus 2,232,000 billable GiB-seconds × $0.0000025), before request fees and egress. It shows the scale of what the instance cap permits, not an expected cost.

### Spend controls

- **No spending limit is enforced.** `max_instances = 2` bounds the number of instances, not dollars: request volume, egress, and Worker usage are not capped by it.
- What exists: scale to zero (`min_instances = 0`), the instance cap, and the CPU and memory limits, all wired from Terraform variables (checked offline by `check-release.mjs`); Turnstile, the signed edge identity, and application rate limits reduce abusive traffic, but **rejected requests still execute application code and are billed**, so they do not cap spend.
- What does not exist in this repository: a Cloud Billing budget or budget alert, a quota override, or a Cloudflare spend cap. A budget alert notifies; it does not by itself stop spend. Creating budget alerts is a billing change and is left to the owner before launch.

## What the Offline Checks Establish

| Check | Establishes | Does not establish |
|---|---|---|
| `tools/check-release.mjs` and its tests | Every declared file exists and is wired as the release relies on; the evidence inventory satisfies the canonical schema (a documented subset) and the deny-by-default governance rules; the Cloud Run service takes its scaling and size limits from the checked variables, runs `Production`, and has Contact disabled; no tracked file carries a non-placeholder email address; this runbook's cost and privacy statements are present and scoped. | Zero persistence (its persistence checks are **tripwires**: a match proves a regression, no match proves nothing), enforced spending, or live behavior. Full JSON Schema validation and citation resolution are delegated to `EvidenceSchemaValidationTests`, `EvidenceInventoryTests`, and `EvidenceIngestionTests`, which run in the same CI. |
| `tools/check-deployment.mjs`, `check-terraform-plans.mjs`, `check-wrangler-build.mjs` | The Terraform, Wrangler, and workflow invariants in the infrastructure runbook, and plan-only proofs with synthetic credentials. | Anything about a real project, account, or token. |
| Unit, architecture, and published-backend integration tests | Behavior of the code and of a published backend started outside the checkout, on Linux in CI. | A deployed environment. |

Live items not verified by any of the above: a real deployment and the GitHub environment protections, the Worker secret gate against a real Worker, the metadata Cloud Run records on deploy, the OIDC condition against a real GitHub token, actual spend against a billing account, Cloudflare Worker CPU time, provider and mailbox retention, and delivery of a real email.

The repository's private-address check reads tracked files only. It never opens ignored private directories, and its diagnostics name a file and line but never the matched value.

## End-to-End Release Smoke Execution

Run the offline verification suite:

```bash
# 1. Release invariants (declared files, evidence, wiring, address scan, runbook claims)
node --test tools/check-release.test.mjs
node tools/check-release.mjs

# 2. Deployment invariants and workflows
node --test tools/check-deployment.test.mjs
node tools/check-deployment.mjs

# 3. Offline Terraform plan proofs (after both `terraform init -backend=false` runs)
node tools/check-terraform-plans.mjs

# 4. Backend published integration smoke (HTTP server lifecycle outside the checkout)
dotnet test backend/tests/Integration/Rafael.Portfolio.IntegrationTests/Rafael.Portfolio.IntegrationTests.csproj -c Release

# 5. Frontend multi-stage Worker verification
cd frontend
for stage in dev prod; do
  CLOUDFLARE_ENV=$stage npm run build:vinext
  node ../tools/check-wrangler-build.mjs $stage
  npx wrangler deploy --dry-run --env $stage --var PORTFOLIO_BACKEND_URL:https://backend.example.test
done
cd ..

# 6. Skill synchronization
pwsh tools/sync-agent-skills.ps1 -Check
```

## Release Promotion Procedure (Owner Only)

Once all pre-flight checks and smoke executions pass on `develop`, and CI is green for the exact commit:

1. **Verify `develop` is clean and up to date with `origin/develop`**:
   ```bash
   git checkout develop
   git pull origin develop
   ```
2. **Promote `develop` to `main` via fast-forward merge**:
   ```bash
   git checkout main
   git merge --ff-only develop
   ```
3. **Tag the release**:
   ```bash
   git tag -a v1.0.0 -m "Release v1.0.0: initial public portfolio release"
   ```
4. **Push `main` and tag to origin**:
   ```bash
   git push origin main --tags
   ```
5. **Trigger deployment workflow**:
   - In GitHub Actions, navigate to **Deploy**.
   - Dispatch `deploy.yml` with `environment: prod` from `main`.
   - Review and approve the deployment in the GitHub environment gate.

## Rollback Procedure

If any issue is detected after production release:

1. **Backend Rollback (Cloud Run)**:
   - In Google Cloud Console or via `gcloud`, route 100% traffic back to the prior revision:
     ```bash
     gcloud run services update-traffic rafael-portfolio-backend --to-revisions=<PREVIOUS_REVISION>=100 --region=us-central1
     ```
2. **Frontend Rollback (Cloudflare Workers)**:
   - In Cloudflare Dashboard (Workers & Pages > Deployments), roll back to the previous deployment ID instantly, or re-deploy the previous release commit.
