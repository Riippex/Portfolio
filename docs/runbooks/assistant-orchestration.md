# Assistant orchestration

The async Assistant path is implemented behind two closed production gates:
`DisabledModelProvider` and `UnverifiedModelRequestTokenCounter`. No real provider,
tokenizer, tools, vacancy transmission, or model activation is included. An
available provider alone cannot authorize dispatch. Synchronous `Chat` remains
deterministic and never enters the paid path.

## Evidence and rendering

Safety evaluation and the existing verified-evidence selection precede model
admission. An unavailable provider, refused/pending/undocumented question, or
absence of approved claims reaches no paid ports. Existing deterministic copy,
relevance rules, citations and SSE segmentation remain unchanged when disabled.

The host adapter adds whole inventory claims only when both their document and
claim are verified. Canonical statements must be nonempty and at most 400
characters. Existing chunk fields are unchanged. The orchestrator offers at most
six public claim aliases, `C1` through `C6`, for that turn only.

Provider output is untrusted. Before parsing, bound its length to 256 UTF-8 bytes
(also reject over 256 UTF-16 units before the bounded byte-count scan). JSON has
maximum depth 3, exactly `v`, `outcome`, `claims`, and no duplicate properties,
comments, trailing content, unknown properties or escaped aliases:

```json
{"v":1,"outcome":"answer","claims":["C1"]}
```

`answer` requires 1..3 distinct offered aliases; `insufficient` requires an empty
list. The backend emits a fixed neutral lead and each full canonical statement,
never a model-written connector or quote substring. Citations list only selected
claims. Insufficient evidence uses the existing not-documented response. The
model may select and order facts, not author visitor-visible prose.

Chat and model-path SSE render the same response; SSE preserves all canonical
characters. No model-path status, citation or token is emitted before validation
and the accounting epilogue finish (or its deadline passes). There are no new
keep-alive events. Transport writes and network delivery are not timed by the
orchestration deadline.

## Budgets and cancellation

All deadlines share an injected monotonic clock anchored before retrieval:

| Phase | Deadline |
|---|---|
| Reserve, complete-request count, Begin | One shared T0 + 3 seconds |
| Provider wait and bounded output validation | T0 + 11 seconds |
| Accounting epilogue | min(now + 4 seconds, T0 + 15 seconds) |

The total is an orchestration bound plus scheduling jitter, not an HTTP delivery
guarantee. Identity, rate limiting, Turnstile and safety evaluation precede it.
Every asynchronous port is bounded even if it ignores cancellation. A client
abort cancels admission/model work, but not accounting. Late operations are
observed only; their result cannot dispatch, emit, or chain accounting. At two
pending orphaned operations a process-local guard denies new model turns until
they finish. This is deny-only backpressure, not a global concurrency guarantee.
An underlying pending operation may still hold its request objects until it
terminates; observing it does not erase that operation's memory. Real provider
adapters therefore need bounded I/O and retention review before activation.

The token counter receives the identical immutable request later passed to the
provider: system prompt, serialized question/claims, schema and output ceiling.
Only a positive, verified model-specific upper bound within 6,000 input tokens
allows Begin. A future real counter must include any provider-specific overhead;
string length is not a verified token bound. Billable output remains capped at
600. Counter underestimation within the turn allowance is still accounted at
actual usage; exceeding the allowance follows the ledger rules below.

## Accounting and fallback

The existing [model-control ledger](model-control.md) remains authoritative.
Reserve takes the full turn allowance before any dispatch. There is at most one
provider call and no provider retry. Unknown outcomes remain conservatively
held; neither a refusal nor a timeout proves a refund, state, charge or release.

| Condition | Bounded action / result |
|---|---|
| Authoritative Reserve denial | Deterministic fallback; no dispatch |
| Ambiguous Reserve/Begin, count failure | One CancelUndispatched attempt |
| Provider outcome without usage | One Abandon attempt |
| Known usage, even failed status/invalid output | Complete with captured immutable usage |
| Complete Applied | One Settle |
| Complete InvalidUsage/AllowanceExceeded | No retry or Settle; deterministic fallback |
| First Complete InvalidState/NotFound/StateInvalid | No retry or inferred success |
| First Complete ambiguous | At most one identical Complete retry |
| Retry InvalidState or ambiguous | At most one Settle probe |
| Settle Applied or InvalidTariffOrPolicy | Settlement confirmed; permit removed |
| Other/unknown Settle result | P2 deterministic fallback; no further action |

All cleanup, retries and probes share the one epilogue budget. No Abandon follows
an ambiguous Complete: it could make completed usage unnecessarily unresolved.
Only a confirmed Settle allows validated output under the chosen **P2** policy.
Validation runs first, but failed validation never skips known-usage accounting.

Important uncertainty cases:

- Cancel NotFound does not exclude a pending Reserve committing later.
- Cancel InvalidState does not prove Begin committed; an already-resolved
  reservation can produce the same result.
- A lost Complete response may have been Applied, InvalidUsage or
  AllowanceExceeded. Stored charge may exceed the reservation; do not infer
  Active state or worst-case charge from missing confirmation.
- Settle InvalidTariffOrPolicy resolves at the retained charge and removes the
  permit immediately. It is not a lease-expiry fallback.
- AllowanceExceeded may settle at retained/larger charge or leave Uncertain
  without a call in flight if pricing/counters cannot finalize it. Reclaim may
  remove that permit without resolving the reservation or assigning expiry.
- Unknown in-flight calls keep their permits beyond lease expiry until operator
  reconciliation. Late completion never triggers automatic request-path recovery.

## Privacy, tests and activation

All content stays in request memory. No raw payload, new log field, transcript,
visitor-linked model memory or application store is introduced. Existing ledger
metadata retention remains unchanged. Provider retention and provider-side usage
correlation remain separate activation decisions.

Tests use synthetic providers/counters, clock-controlled delays, and decorators
over the existing accounting implementation. They exercise lost responses,
invalid output, uncertainty, retry bounds, deadlines, selected citations and
disabled host composition. Emulator tests independently exercise real Firestore
transactions; without an emulator those checks are incomplete, never skipped.

Before activation: review real counting, model pricing/version and retention;
provide an owner-controlled reconciliation entry point; verify shared ledger,
IAM and TTL; deploy access controls; then obtain explicit activation approval.
There is no public recovery API. This slice is not evidence of a live model or
production rollout.
