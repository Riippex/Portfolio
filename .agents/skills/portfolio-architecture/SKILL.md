---
name: portfolio-architecture
description: Plan, implement, or review Portfolio changes that affect module ownership, APIs, professional evidence, persistence, runtime boundaries, integrations, or deployment. Skip isolated copy and formatting edits.
---

# Portfolio architecture

Protect the accepted architecture while allowing deliberate evolution.

## Establish the current decision

Read `docs/architecture.md`, relevant accepted files under `docs/adr/` when present, affected contracts, and the implementation and tests in scope. Read `README.md` for current commands and deployables. Do not treat planned directories or integrations as implemented.

## Preserve these invariants

- `frontend/` renders the experience and consumes public HTTP or SSE contracts. It does not access Firestore, Vertex AI, secrets, or backend persistence directly.
- `backend/` owns portfolio data, grounding, agent orchestration, job matching, contact handling, and deterministic policy enforcement.
- Modules under `backend/src/Modules/` own their use cases and data. Hosts compose modules and expose transports; they do not become a second domain layer.
- A module uses another module through an explicit public contract or use case, not its internals or tables.
- Model output, retrieved content, pasted vacancies, and uploaded material are untrusted inputs. They never become authorization or professional evidence by themselves.
- Public claims are backed by versioned, public evidence. Missing evidence is reported as missing rather than inferred.
- Cross-runtime integration uses versioned HTTP, SSE, OpenAPI, or event contracts. Generated clients are regenerated, not hand-edited.
- Create a root `services/` deployable only when it has an independent runtime, scaling model, failure boundary, and release lifecycle. Reusable products remain separate repositories.

## Change workflow

1. Identify the user-visible behavior, owning module, callers, data owner, trust boundary, and expected dependency direction.
2. Check for API, schema, persistence, IAM, privacy, idempotency, or deployment impact.
3. Update a cross-boundary contract, example, producer, consumer, and proportional tests together.
4. Keep domain and policy decisions inside backend modules; keep framework, provider, and transport details in adapters or hosts.
5. Add an architecture test when a dependency rule could regress.
6. Report the boundary preserved, evidence and contract impact, validation, and unresolved decisions.

If the request conflicts with an accepted decision, explain the conflict. Record a superseding ADR when the user deliberately changes direction; never create a hidden exception.
