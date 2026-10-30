# ADR 0004: Opt-in control-client startup preparation

- Status: Accepted; environment activation requires separate authorization
- Date: 2026-10-30
- Scope: Backend operational lifecycle; the default request lifecycle is unchanged

## Context

The model's unchanged three-second preparation budget includes reservation,
verified token counting and begin-call admission. Lazy Firestore SDK creation,
authentication and its first transactional round trips can consume most of that
budget on a cold process. Extending the request deadline or skipping admission
would weaken existing controls. Eager SDK construction alone does not establish
the first authenticated database connection.

The existing default consent contract places credentials, reservation, counting
and generation behind the visitor's affirmative choice. Operational startup
authentication would be an explicit exception to that literal credentials
promise. It must not be introduced as an invisible performance optimization.

## Decision

Provide a dormant, off-by-default `Assistant:ModelControl:PrepareClientOnStart`
option. It requires the exact value `true` and the same fully enabled, reviewed,
tariff-valid, project-matching Vertex/ledger pair required for model composition.
Missing, false, differently cased or incomplete settings do not prepare a client.
Accepting this ADR and authorizing an environment's configuration are separate
owner decisions. The option remains off by default.

When explicitly adopted and enabled, before the Web host starts serving requests:

1. Build the shared Firestore client asynchronously with the service/operator's
   existing ADC, under one five-second startup budget.
2. Invoke one nontransactional read of `model_control/state`. The SDK's existing
   transient stream retries remain subject to that same budget; no application
   retry is added. This reads existing operational metadata, not visitor data.
3. Validate the root schema, sanity and policy version. Publish only the client
   for ordinary ledger operations, never the snapshot, permits or an allowance.
4. Keep the client unavailable for the process after failure, missing/corrupt
   state, policy mismatch or timeout. Observe late completion without publishing
   readiness; a late client build cannot chain a root read. Host startup and the
   deterministic assistant remain available. Another attempt needs a process
   restart; no visitor request starts or retries preparation.

The timeout bounds the local startup wait; cancellation does not prove that an
already dispatched authentication/read operation was cancelled remotely. Late
results are observed and discarded rather than promoting client readiness.

This lifecycle invokes neither Vertex credentials nor CountTokens/generation.
Its control-store authentication can populate the SDK's process-wide ADC cache;
the absolute default promise of no credentials before visitor consent therefore
does not describe this opt-in lifecycle. Visitor input, model-specific credential
access, counting and generation remain behind the existing request consent gate.

Preparation creates no database, root, counter, reservation, refund, recovery
operation or durable record. Every request still reads authoritative state in
its normal transactions. The three-, eleven- and fifteen-second request budgets,
adapter deadlines, finite allowances, fallback and public response formats stay
unchanged. It does not reset any request clock or demonstrate production readiness.

## Consequences and review gates

- One startup metadata read per eligible process can incur Firestore operations
  and authentication/network work; it is not a total invoice cap or free service.
- A restarted or scaled process repeats the bounded operational preparation.
  SDK initialization is not proof of model permission, provider compatibility,
  capacity, visitor consent or successful settlement.
- Missing or stale state fails closed. A healthy startup read cannot authorize
  later requests or hide subsequent policy changes/corruption.
- Operational logs contain bounded outcomes and duration only, without credential
  contents, root fields, prompts, responses, visitor identifiers or exception text.
- This decision does not deploy, apply Terraform, activate an environment or change
  cloud permissions. Keep the option unset until the owner authorizes the target
  environment; a live synthetic model test still requires its separate bound.

## Sources checked

- [Firestore 4.4 builder](https://github.com/googleapis/google-cloud-dotnet/blob/Google.Cloud.Firestore-4.4.0/apis/Google.Cloud.Firestore/Google.Cloud.Firestore/FirestoreDbBuilder.cs)
- [Firestore 4.4 transaction lifecycle](https://github.com/googleapis/google-cloud-dotnet/blob/Google.Cloud.Firestore-4.4.0/apis/Google.Cloud.Firestore/Google.Cloud.Firestore/Transaction.cs)
- [Google Auth 1.73 process-wide ADC cache](https://github.com/googleapis/google-api-dotnet-client/blob/v1.73.0/Src/Support/Google.Apis.Auth/OAuth2/DefaultCredentialProvider.cs)
