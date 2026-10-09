# Data handling policy

Persistence is deny-by-default. A feature may store data only after it names the
data owner, purpose, visibility, store, retention or expiry, deletion path, log
policy, and authorization boundary.

## Data classes

| Class | Portfolio examples | Allowed handling |
|---|---|---|
| Public evidence | Curated biography, project descriptions, source URLs, evidence versions | Persist durably and version it. Public claims may use only this class. Removal is a versioned change followed by cache or index invalidation. |
| Durable operational record | Consent and delivery status for a contact request, idempotency receipts, bounded policy decisions | Backend-owned persistence only when the behavior requires it. Store the minimum fields and define retention and deletion before release. |
| Session-transient input | Chat turns, pasted vacancies, model responses, retrieved chunks, SSE payloads | Process in memory for one request by default. If session continuity is introduced, use an ephemeral store with an explicit TTL and deletion path; do not place it in the durable database. |
| Short-lived control state | Rate-limit counters, Turnstile outcomes, deduplication keys, correlation IDs | Store only opaque or pseudonymous identifiers in a TTL-backed cache. Cache loss must not corrupt durable facts. |
| Sensitive ephemeral material | Raw resumes, uploads, contact text before delivery, provider request and response bodies | Validate, bound, and redact before use. Do not retain by default or include in logs, analytics, traces, events, or model memory. |
| Secrets and credentials | Cookies, access or refresh tokens, API keys, service credentials | Keep only in approved secret or secure session stores. Never place them in application persistence, Terraform state values, URLs, logs, prompts, browser storage, or public bundles. |

## Model control ledger

The Assistant's model budget ledger is the one approved durable operational record for paid
model work (see [model control](runbooks/model-control.md)).

- Owner and store: the backend, in one shared Cloud Firestore database that no frontend code can
  reach; there is no public ledger API.
- Purpose: admit paid model calls under the approved daily, monthly and concurrency limits.
- Visibility: backend only.
- Content: opaque reservation ids, UTC period keys, integer micro-USD and token counts, policy,
  tariff and model versions, states and timestamps. Never an IP address or country, prompt,
  answer, contact data, CV text, tool payload or transcript.
- Retention: day and month counters expire 40 days after their accounting period ends, and a
  resolved reservation 40 days after its month ends (or after it was resolved, if later).
  Unresolved reservations (active or uncertain) have no expiry and are never deleted by TTL or
  cleanup until an explicit reconciliation resolves them. Firestore TTL deletion is
  asynchronous; logical expiry does not wait for it.
- Deletion: expiry and the backend's bounded cleanup. There is no per-visitor record to delete.
- Logs: outcome codes and counts only.

## Product defaults

- Anonymous chat has no durable transcript or cross-session memory.
- Pasted job descriptions and job-match output are ephemeral and cannot add
  facts to the professional profile.
- Agent memory is disabled until identity, consent, allowed values, retention,
  and deletion are implemented. If enabled, store only normalized allowlisted
  preferences explicitly requested by the user; never store secrets, free-form
  facts, permissions, or professional claims.
- Contact handling is not considered persistent by default. Before storing a
  submission, define consent, recipient, encryption, retention, deletion, and
  abuse controls. Prefer relaying the message and retaining only minimal
  delivery metadata when product requirements allow it.
- Retrieval indexes contain only approved public evidence plus source,
  visibility, and content-version metadata. Private repositories, raw resumes,
  local notes, and user session content are excluded.
- Shared events contain allowlisted identifiers and facts, never raw prompts,
  transcripts, vacancies, resumes, contact bodies, tokens, or provider payloads.

## Observability

Logs and traces may record correlation IDs, source IDs, model and prompt
versions, tool names, policy decisions, latency, status, and bounded reason
codes. They must not record raw user or model content, contact details, secrets,
hidden prompts, or chain-of-thought. Prefer counters and classifications over
payloads.

## Deletion

Ephemeral data expires automatically and supports explicit early deletion when
a session exists. Durable user data requires an owner-scoped deletion path.
When data also exists in object storage, an index, or a provider system, record
a tombstone and retry cleanup; do not report deletion complete until removal is
confirmed. Cleanup metadata may outlive the deleted record only as long as
required to finish and verify removal.

## Review gate

Any new database table, object, cache key, vector, event field, analytics event,
or log field must declare its data class and lifecycle. If the lifecycle is not
defined, keep the value in process memory and mark the persistence decision as
unresolved.
