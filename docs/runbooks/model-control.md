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
| `model_control/state` (one) | schema version, policy version, last UTC day and month keys, the active permit set (reservation id to lease end, at most 2), live reservation count, an epoch that advances with every admission and cleanup |
| `model_control_periods/day_YYYY-MM-DD`, `month_YYYY-MM` | schema version, kind, period key, charged micro-USD, `expiresAt` |
| `model_control_reservations/{id}` | opaque id (bounded token), state, policy, tariff and model versions, day and month keys, reserved and charged micro-USD, call and token limits and usage, in-flight flag, timestamps, and `expiresAt` **only once the reservation is resolved** |

Every document carries `schemaVersion`, and the backend accepts exactly the supported value: a missing, malformed, unknown or out-of-range version (including a large integer that would wrap to the supported one when narrowed) is rejected before any other field is read, grants no paid work and changes nothing.

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
* **A lease is not proof the provider finished.** A permit is held for a lease (5 minutes, longer
  than a turn's 15 second bound), but a remote call can outlive its caller and the lease. When a
  lease has ended, the next reservation frees the permit **only if nothing can still be running
  for it**: no call was dispatched, or every dispatched call reported its usage. Those
  reservations become `Lapsed` (charge kept). A permit whose reservation has a call **in
  flight**, or whose reservation is missing or unreadable, stays held; its reservation becomes
  `Uncertain` and keeps the permit and the charge until it is explicitly reconciled (see
  Recovery). Waiting longer, restarting or a caller timeout never releases it, so two crashed
  dispatched calls disable new paid work until an operator resolves them. The lease is an
  accounting bound, **not** control over what a remote provider is still doing.
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

Expiry depends on whether the obligation is resolved.

* **Unresolved reservations (`Active`, `Uncertain`) have no expiry.** They are stored without an
  `expiresAt` field, and Firestore TTL only deletes documents that have one, so TTL cannot
  remove evidence of an open obligation. The backend's cleanup also refuses to delete them, even
  if a stray expiry were present. They count toward the 2,000 live-reservation capacity until
  resolved, so unresolved evidence stays bounded and, if it ever fills the ceiling, denies.
* **Resolved reservations (`Settled`, `Cancelled`, `Lapsed`) expire 40 days after the end of their
  month**, or 40 days after they were resolved if that is later, so a late reconciliation keeps
  its evidence for 40 days.
* **Day and month counters expire 40 days after their period ends.** A closed period no longer
  affects any admission. If a reservation is reconciled after its counters expired, the
  reservation evidence is updated and the permit released without recreating a counter.

Firestore TTL deletes asynchronously (typically within hours, not guaranteed), so correctness
never depends on the physical deletion. `CleanupExpiredAsync` removes expired resolved metadata,
lowers the live count and advances the epoch; it also runs once, bounded, when the live count
reaches its capacity. `ReconcileCapacityAsync` sets the live count from an observed total after
TTL deletions, race-safely: it counts, then writes only if the control document's epoch and count
are exactly what they were before counting, otherwise it changes nothing (`ConcurrentChange`) and
the caller retries. An admission or cleanup during the count can therefore never be overwritten,
and a deletion during the count can only make the total too high, which is the safe direction.

## Initialization

A missing control document denies every reservation; the request path never creates counters.
The owner initializes once, deliberately, by starting one backend with
`Assistant__ModelControl__InitializeStore=true`, then removes the setting. Initialization never
overwrites an existing document.

## Recovery

Recovery is explicit, operator-driven and conservative. No request path, timer or HTTP endpoint
calls it.

* **Find unresolved reservations** with `IModelControlRecovery.ListUnresolvedAsync` (accounting
  metadata only) or in the Firestore console (`model_control_reservations` with state `Active` or
  `Uncertain`). Ask whether the provider call really ran, using the provider's own usage records.
* **Resolve each one** with `IModelControlRecovery.ReconcileAsync`, which also releases its
  permit and adjusts only that reservation's **own** day and month counters:
  * `NotDispatched`: the call is confirmed never to have run; the whole charge is refunded.
  * `Completed` with the provider-confirmed usage of the call that was in flight; the charge
    becomes its tariff cost (it can be higher than reserved).
  * `ChargeAsReserved`: the usage cannot be established; the reserved charge is accepted as final.
  A resolved reservation cannot be reconciled again. The port has no host entry point yet, so
  until a maintenance tool exists the same steps are done by hand in the console: set the
  reservation's state, correct the counters by the same amount, remove its entry from the
  control document's `permits`, set its `expiresAt` per the retention rules, and advance `epoch`.
* If a counter is lost or corrupt the ledger denies. Compare the period counter with the sum of
  its reservations' `chargedMicroUsd`, correct it, and confirm the control document's
  `lastDayKey`/`lastMonthKey`. Never recreate a counter at zero for a period that already spent
  money.
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
