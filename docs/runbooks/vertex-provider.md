# Vertex provider and operator activation

The text-only REST adapter is implemented, but disabled by default. Installing
it or deploying its code does not enable inference. No API keys are required:
the runtime uses managed identity through Application Default Credentials.
Do not activate, send visitor input, enable APIs, change IAM, or change provider
retention as a side effect of code validation.

## Model, location and price review

Only `gemini-3.1-flash-lite` is accepted. Its documented locations are `us`, `eu`
and `global`; the backend's Cloud Run region is a separate setting. Its listed
retirement is May 7, 2027 or later. Recheck the
[official model page](https://docs.cloud.google.com/gemini-enterprise-agent-platform/models/gemini/3-1-flash-lite)
before activation.

Reviewed standard on-demand text prices on 2026-10-20: global USD 0.25 input /
1.50 output per million tokens; non-global USD 0.275 / 1.65. Output includes
reasoning. These are not batch, cached-input or priority prices; check the
[pricing table](https://cloud.google.com/gemini-enterprise-agent-platform/generative-ai/pricing)
for the chosen endpoint. The provider refuses a tariff below these reviewed
floors or valid more than 31 days into the future. There are no default prices.
At these non-global rates, a 6,000-input/600-output reservation is 2,640 micro-USD.
The existing shared USD 0.10/day and USD 1/month limits are unchanged and are
model-admission limits, not total Google Cloud invoice caps.

## Explicit server configuration

Missing or inconsistent configuration composes `DisabledModelProvider` and the
denying counter, preserving the deterministic assistant. All the following must
be explicitly reviewed and set by the operator, not a visitor:

| Configuration | Requirement |
|---|---|
| `Assistant:Vertex:Enabled` | Exactly `true`, default false |
| `Assistant:Vertex:ProjectId` | Valid project ID, identical to the ledger project |
| `Assistant:Vertex:Location` | Explicit `us`, `eu` or `global`; no inferred location |
| `Assistant:Vertex:DataPolicyReviewed` | Exactly `true` only after external-retention review |
| `Assistant:Vertex:TokenCountingReviewed` | Exactly `true` only after checking the pinned text-only count contract |
| `Assistant:Vertex:RecoveryReady` | Exactly `true` after verifying operator access and recovery procedure |
| `Assistant:ModelControl:Firestore:ProjectId`, `DatabaseId` | Both configured, shared across stages |
| `Assistant:ModelControl:Tariff:*` | Explicit model, version, integer rates and near-term expiry |

Environment variables use double underscores in place of colons. These switches
are assertions of completed operator checks, not permission to skip them. Default
deployment inputs do not activate this provider; rollout remains a separately
reviewed change. Preserve Terraform ownership and do not create drift
by silently editing the service outside its reviewed configuration.

### Terraform-owned preparation

`deployment/gcp/vertex.tf` defines one optional `vertex` object. Its default is
`null`: no Vertex environment, tariff, custom role or predict binding is planned,
including for existing dev/prod inputs. The shared ledger project/database pair
remains independent; the shared stack still owns its APIs, database and TTLs.

The [stage example](../../deployment/gcp/terraform.tfvars.example) shows a
disabled preparation object. Location must explicitly be `global`, matching the
recorded owner choice/current visitor notice, and tariff version, integer rates
and date are required with no defaults. Terraform checks format and reviewed
price floors; the backend remains authoritative for expiry, the 31-day horizon,
budget, consent and all runtime checks. Changing the location/model requires a
new review, not a visitor-provided override.

All five booleans default to false: `enabled`, `grant_predict_access`,
`data_policy_reviewed`, `token_counting_reviewed`, `recovery_ready`. Setting
`enabled` requires the other four. A configured object requires the same-project
shared ledger; granting predict also requires the service phase and dev/prod.
These checks establish configuration consistency, not that a review happened.

`grant_predict_access` independently plans a stage-specific project custom role
with exactly `aiplatform.endpoints.predict` and one additive binding to the
backend runtime account. It grants nothing to CI, the Worker or visitors. This
permission supports [custom roles](https://cloud.google.com/iam/docs/custom-roles-permissions-support)
and is required by [CountTokens](https://docs.cloud.google.com/gemini-enterprise-agent-platform/reference/rest/v1/projects.locations.endpoints/countTokens)
and [generation](https://docs.cloud.google.com/gemini-enterprise-agent-platform/reference/rest/v1/projects.locations.publishers.models/generateContent).
The role is **project-scoped, not a per-model restriction**: the backend pins the
model, but a compromised runtime identity could invoke other models/endpoints
in that project. Do not describe this grant as an IAM model allowlist. No broad
predefined role or service-account key is needed. The pinned provider's
[custom-role resource](https://github.com/hashicorp/terraform-provider-google/blob/v6.50.0/website/docs/r/google_project_iam_custom_role.html.markdown)
has soft-delete/name-reuse constraints; review the actual saved plan before
recreating a previously deleted role.

Permission-only preparation can keep `enabled = false`; it still needs explicit
IAM/apply approval. Model-disabled rollback uses `enabled = false` without
deleting counters or making any claim that a remote request was cancelled.
Revoking the access gate removes the binding/role and needs its own reviewed
plan. Terraform never sets `InitializeStore`; initialization and recovery remain
separately authorized operator actions. No setting is applied by merely editing
or testing these definitions. Main, production and Contact stay separate gates.

`deployment/control-ledger` owns both shared project APIs, gated with the single
database foundation. API enablement alone grants no runtime prediction permission,
sets no review switch and sends no model input. Follow the [foundation sequence](model-control.md#foundation-activation)
before the separate provider activation below; stage stacks must not own a second
copy of these shared API resources.

## Request and accounting contract

The adapter sends one text-only request with system instructions, question and
canonical claims, JSON output schema, one candidate, `MINIMAL` thinking, and at
most 600 output tokens. It does not send tools, vacancies, raw CVs, uploaded
files, history, cache-creation requests or thoughts to the visitor.

CountTokens receives the same model input and generation configuration used for
inference. Only a positive count up to 6,000 with a matching TEXT-only modality
breakdown is accepted; no byte/character heuristic replaces it. See the
[CountTokens request contract](https://docs.cloud.google.com/gemini-enterprise-agent-platform/reference/rest/v1/projects.locations.endpoints/countTokens).
The counter's verified mode is restricted to this reviewed text-only request;
new modalities, model changes or tools require another review. Live endpoint
compatibility/count parity/latency must be checked with synthetic input before
marking token counting reviewed. The documentation describes CountTokens as
[uncharged](https://docs.cloud.google.com/gemini-enterprise-agent-platform/models/capabilities/get-token-count);
it is still bounded by admission and the shared preparation deadline.

A process-memory, weak-key receipt binds a successful count to that exact
immutable request object and can authorize inference only once. A different
request cannot reuse it. The provider has no automatic inference retry.

Inference adds only billing metadata: `portfolio_reservation` (the existing
opaque ledger ID) and `portfolio_prompt` (`canonical-claims-v1`). These are not
model input and CountTokens has no labels field. Never substitute a visitor ID,
IP, message or secret. Google forwards labels to billing for PayGo requests;
visibility may be delayed. See [billing-only labels](https://docs.cloud.google.com/gemini-enterprise-agent-platform/models/capabilities/add-labels-to-api-calls).
A label or an absent billing row is not proof that a remote call finished.

Usage is parsed independently of output validity. Billable output is candidates
plus thoughts, cross-checked against total minus prompt; missing, negative,
overflowing or inconsistent usage remains unknown. Non-STOP, malformed, blocked
or truncated output never becomes an answer, but known usage still reaches the
ledger. Thought text is discarded. The fields are documented in
[GenerateContentResponse](https://docs.cloud.google.com/gemini-enterprise-agent-platform/reference/rest/v1/GenerateContentResponse).
If actual input exceeds the count receipt, preserve actual usage and disable
new inference in that process until reviewed/restarted. Do not retry to repair it.

Requests and response bodies are limited to 64 KiB. OAuth headers are redacted;
redirects are disabled. There are no payload or exception-message logs, new
visitor-linked fields, durable receipts, or response transcripts.

## Retention and least privilege

Application zero-transcript handling does not imply provider zero retention.
Google documents abuse-monitoring logging and implicit in-memory/durable caching;
availability and configuration of exclusions must be verified for this project.
Do not enable `DataPolicyReviewed` or advertise zero retention based solely on
the absence of an application database. See [provider retention controls](https://docs.cloud.google.com/gemini-enterprise-agent-platform/resources/zero-data-retention).
Before activation record the owner's location/retention choice, validate the
project's actual cache/logging settings, and provide accurate visitor disclosure.

Owner decision on 2026-10-20: use `global` and the provider's documented standard
retention, not a zero-retention arrangement. This is a configuration decision,
not activation approval or consent on behalf of visitors. Global processing has
no fixed-country guarantee. Confirm the applicable project terms and retention
before writing the visitor notice; do not invent one universal deletion period.

### Visitor authorization gate

The [versioned request contract](../contracts/assistant-chat.md) and UI now
implement the optional, initially unchecked choice. The visible notice identifies
Google Cloud, global processing, standard retention, data sent and purpose;
privacy details name the operator/contact and explain withdrawal and no
application transcript/consent storage. Contact consent remains separate.

With default configuration, `AssistantService` checks current affirmative consent before the entire
orchestrator in async JSON and SSE, so credentials, reservations, **CountTokens**
and generation are unreachable without it. The host's strict optional-field
converter denies malformed choices rather than coercing them. Missing, refused
or stale choices retain deterministic chat and normal security checks. A client
checkbox, operator flag, Turnstile or allowlist membership alone cannot grant it.

The off-by-default startup control-client proposal in [ADR 0004](../adr/0004-opt-in-control-client-startup-preparation.md)
is **not accepted or authorized for activation**. Its dormant setting is
`Assistant:ModelControl:PrepareClientOnStart`, exact `true`; it additionally
requires the same enabled/reviewed, tariff-valid, project-matching provider pair.
If separately adopted, it permits one five-second operational Firestore client
preparation/root-read invocation before serving requests (existing SDK transient
retries share that bound). It authenticates to the control store before visitor
consent, explicitly changing the default credentials lifecycle; it never calls
the Vertex credential path, CountTokens or generation, or writes ledger state.
Only the client is reused. Failure leaves that process's opt-in store unavailable
and deterministic fallback intact; late tasks cannot publish readiness. No
per-request warmup, deadline reset, retry loop, new allowance, infrastructure
setting or activation authorization is introduced. Keep this flag unset until
the owner accepts the proposal and authorizes the target environment separately.

The choice remains in component/request memory, resets on reload and can be
withdrawn for subsequent submissions, not recalled from already dispatched
processing. No ledger field, durable consent register, browser storage or new log
field is added. If accountability requires retained proof, separately define its
minimal lifecycle before implementing it. Automated tests exercise direct HTTP,
SSE, application calls, the real adapter over synthetic transport, withdrawal,
notice parity and the frontend choice.

These controls do not establish legal compliance or complete project activation.
Before setting `DataPolicyReviewed`, review the final notice against actual
project terms/cache/logging settings and applicable obligations, and verify that
the privacy request/contact channel works. Production release is still gated.

The owner reviews API enablement and a minimal runtime role. CountTokens and
GenerateContent require `aiplatform.endpoints.predict`; do not grant owner/editor
or create service-account keys. See the [official IAM method requirements](https://docs.cloud.google.com/gemini-enterprise-agent-platform/reference/rpc/google.cloud.aiplatform.v1).
Firestore access remains scoped to the dedicated ledger database. No IAM/API/
database mutation is performed by this adapter or the operator CLI.

## Operator recovery (not HTTP)

The published Web executable has a CLI-only mode that exits before configuring
or starting the Web host. It reads the explicit ledger configuration from the
environment and uses the operator's ADC. It never loads model credentials or
calls the model. Run only with separately authorized operator access:

```text
dotnet Rafael.Portfolio.Web.dll --model-control list
```

This reads at most 50 unresolved reservations, accounting metadata only. After
checking the remote provider state, identify the exact reservation and use one
of the following forms; replace the synthetic ID with the intended target:

```text
dotnet Rafael.Portfolio.Web.dll --model-control reconcile res_example_001 not-dispatched --apply --confirm res_example_001 --provider-state-checked
dotnet Rafael.Portfolio.Web.dll --model-control reconcile res_example_001 completed --apply --confirm res_example_001 --provider-state-checked 500 100
dotnet Rafael.Portfolio.Web.dll --model-control reconcile res_example_001 charge-as-reserved --apply --confirm res_example_001 --provider-state-checked
```

For `completed`, the two integers are the provider-confirmed input and total
billable output for the pending call, reasoning included. Preserve any usage
already recorded by earlier calls. A timeout or missing billing row never proves
`not-dispatched`. `charge-as-reserved` is an explicit acceptance of the retained
charge, not a substitute for checking whether remote work can still be running.
The existing [recovery rules](model-control.md#recovery) remain authoritative.

Unknown commands, extra arguments, missing confirmation or invalid usage touch
no store. Exit codes: 0 confirmed operation, 2 invalid/unconfigured invocation,
3 refused or unconfirmed operation. Exceptions never print credentials or raw
provider content. An unconfirmed outcome must be inspected before another action.
There is no HTTP endpoint, scheduler, request-path recovery or automatic refund.

## Activation sequence

1. Verify this commit's tests and the real ledger emulator checks.
2. Apply the recorded global/standard-retention decision, verify actual provider
   terms/settings and the notice/contact channel, and rerun visitor authorization checks.
3. Review/apply the gated shared ledger, runtime IAM/API and configuration changes.
4. Verify operator ADC/access and recovery without weakening uncertainty rules.
5. Authorize a bounded synthetic count/inference smoke; verify complete-request
   token parity, billable reasoning, actual pricing and the 15-second budget.
6. Only then enable reviewed configuration and roll out access controls and the
   provider together. Main promotion, DNS and production deployment need explicit
   release authorization. Roll back paid work by disabling the provider, not by
   deleting counters or refunding unknown reservations.
