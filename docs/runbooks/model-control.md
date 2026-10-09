# Model control ledger

The Assistant admits paid model work only through a backend-owned ledger. This runbook
defines what it stores, who owns it, how it behaves when things go wrong, and how to test
and operate it. No paid model is enabled by this feature: the real model provider stays
disabled until a separate, explicit activation.

**The ledger is a model-cost gate, not a total invoice cap.** It limits what the backend
admits for model calls under the approved tariff. It does not cap the Google Cloud bill, other
products, network egress, Firestore operations, or a provider outage that charges without a
response.

## Policy

One portfolio-wide allowance, shared by every stage (dev and prod), replica and restart:

| Control | Limit |
|---|---|
| Model cost per UTC day | USD 0.10 (100,000 micro-USD) |
| Model cost per UTC month | USD 1.00 (1,000,000 micro-USD); a new day never restores monthly credit |
| Active permits (concurrent turns) | 2, globally |
| Provider calls per turn | 2 |
| Tokens per turn, cumulative over both calls | 6,000 input and 600 billable output, including system and tool context and reasoning tokens |

Money is integer micro-USD. Costs come from a versioned **tariff** (`Assistant:ModelControl:Tariff`:
version, model id, input and output micro-USD per million tokens, last valid UTC date) and
are rounded up. A missing, malformed, out-of-range or expired tariff, or a policy outside the
approved ceilings, **denies** paid admission; nothing is replaced by a default. The reservation
for a turn is the tariff cost of the full permitted turn (6,000 input and 600 output tokens),
taken atomically against the day, the month and the permit set before any paid work.

## Ownership and sharing

* `deployment/control-ledger` owns the **one** Cloud Firestore (Native mode) database. It is
  gated by `create_database = false`, has deletion protection, takes the stage runtime service
  accounts as input, and grants each only `roles/datastore.user` restricted by an IAM condition
  to this database. It is applied once, by the owner, never from a stage.
* Stage stacks (`deployment/gcp`) never declare the database. They receive its project and
  database id (`control_ledger_project_id`, `control_ledger_database_id`) and pass them to
  Cloud Run as `Assistant__ModelControl__Firestore__ProjectId` and `__DatabaseId`. Dev and prod
  must name the same pair; two databases would be two independent allowances. Both values or
  neither: without them the backend registers a ledger that denies all paid work.
* The backend alone reads and writes the database. The frontend and the browser have no access,
  and there is no public ledger API.

## What is stored

Operational accounting metadata only (data class: durable operational record). Nothing is
stored that identifies a visitor or contains content: no IP address or country, prompt, answer,
contact data, CV text, tool payload or transcript.

| Document | Fields |
|---|---|
| `model_control/state` (one) | schema version, policy version, last UTC day and month keys, the active permit set (reservation id to lease end, at most 2), live reservation count |
| `model_control_periods/day_YYYY-MM-DD`, `month_YYYY-MM` | kind, period key, charged micro-USD, `expiresAt` |
| `model_control_reservations/{id}` | opaque id (bounded token), state, policy, tariff and model versions, day and month keys, reserved and charged micro-USD, call and token limits and usage, in-flight flag, timestamps, `expiresAt` |

Logs carry correlation hashes, outcome codes and counts only.

## Lifecycle of a turn

1. `TryReserve` reserves the worst case and one permit (denied: budget, permits, capacity,
   duplicate id, unusable tariff or policy, missing or invalid state, store outage).
2. `TryBeginCall` authorizes **each** provider call within the remaining call and token
   allowance. A turn has at most two calls, never concurrent.
3. `CompleteCall` records the reported usage. Malformed usage makes the outcome unknown;
   usage above the allowance is charged at its larger actual cost.
4. `Settle` reconciles the charge to the tariff cost of the confirmed usage. The refund is
   applied to the reservation's **own** day and month counters, never to the current ones.
5. `CancelUndispatched` refunds a reservation only if no provider call began.
6. `Abandon` records an unknown outcome (timeout, disconnect, provider error after dispatch).

A reservation id is idempotent: a second reservation with the same id is denied, and a second
call cannot start while one is in flight or after the call limit, so a duplicate cannot dispatch
paid work twice.

## Failure rules

* **Unknown outcome stays charged.** Timeouts, disconnects, invalid usage, a lost permit and TTL
  never refund. The charge remains in the counters.
* **Permits and the lease.** A permit is held for a lease (5 minutes, longer than a turn's 15
  second bound and any provider call deadline). Closing a connection does not free it. After
  the lease ends, the next reservation reclaims the permit and marks the reservation
  `Uncertain`; the charge is kept. The lease is an accounting bound, **not** control over what
  a remote provider is still doing.
* **Corrupt, lost or missing state denies.** Negative or oversized balances, wrong types, an
  unknown schema or policy version, a missing current-period counter, a clock behind the
  recorded high-water mark, and a store that was never initialized all deny and change nothing.
  Arithmetic never wraps.
* **Store outage or contention denies.** Every operation is one bounded transaction (5 attempts,
  10 second deadline). A timeout can leave a server-side transaction holding locks until Firestore
  expires it (about a minute), which only ever causes denials. The backend then answers with
  the bounded deterministic fallback.
* **No cache can grant spending.** A denial for budget, permits, capacity or an unavailable store is remembered in the backend process for 1 to 5 seconds so a burst of doomed requests does not queue transactions on the control document. It can only refuse; spending authority comes from the transaction alone.

## Retention and cleanup

Counters and reservation metadata expire **40 days after the end of their accounting period**
(a reservation, after the end of its month), written to `expiresAt`. Firestore TTL deletes
them asynchronously (typically within hours, not guaranteed), so correctness never depends on
the physical deletion; reading code treats expiry logically. `CleanupExpiredAsync` removes
expired inactive metadata and lowers the live count; `Active` reservations are never removed. It also runs once,
bounded, when the live count reaches its capacity (2,000). `ReconcileCapacityAsync` sets the
count from an observed total after TTL deletions.

## Initialization

A missing control document denies every reservation; the request path never creates counters.
The owner initializes once, deliberately, by starting one backend with
`Assistant__ModelControl__InitializeStore=true`, then removes the setting. Initialization never
overwrites an existing document.

## Recovery

* Permits stuck after a crash free themselves when the lease ends (next reservation).
* If a counter is lost or corrupt the ledger denies. Recovery is an owner action in the Firestore
  console: stop paid use, compare the period counter with the sum of its reservations'
  `chargedMicroUsd`, correct it, and confirm the control document's `lastDayKey`/`lastMonthKey`.
  Never recreate a counter at zero for a period that already spent money.
* A new tariff or policy requires a new version and a deliberate migration of the control
  document; a mismatch denies.

## Tests

`dotnet test backend/tests/Unit/...` proves the rules with a test-only in-memory adapter. That
does not prove Firestore behaviour. `backend/tests/Emulator` runs the real adapter against the
Firestore emulator: independent clients, concurrency, restart, stage sharing, duplicate ids,
UTC day and month boundaries, overflow, corrupt and lost state, invalid usage, tariffs,
uncertain outcomes, capacity, cleanup and store failure. It needs `FIRESTORE_EMULATOR_HOST`
and uses throwaway project ids; **without the emulator every test fails as INCOMPLETE, it is
never skipped or passed.** CI starts the emulator in its own job.

```bash
docker run -d --name firestore-emulator -p 8080:8080 google/cloud-sdk:emulators \
  gcloud emulators firestore start --host-port=0.0.0.0:8080
FIRESTORE_EMULATOR_HOST=127.0.0.1:8080 dotnet test backend/tests/Emulator/Rafael.Portfolio.EmulatorTests
```

## Known limits

* Denial under heavy contention is by design; the paid path is rarely used and edge-limited.
* The IAM condition on the database and the TTL behaviour are verified by the owner's first
  apply, not by the offline checks; a mismatching condition denies access, so paid work stays off.
* Model prices change. The tariff has an expiry date and must be rechecked before activation.
* Provider data retention and provider-side billing review remain a gate before activation.
