# Architecture

The portfolio is a product demonstration, not a static résumé. Its public claims must be traceable to versioned evidence.

## Deployables

- `frontend/`: Next.js application deployed to Cloudflare Workers.
- `backend/`: .NET 10 modular monolith deployed to Google Cloud Run.
- `services/`: created only when a process needs an independent runtime, release, or scaling model.

## Backend boundaries

`backend/src/Modules` contains business capabilities. Each module owns its domain, use cases, infrastructure adapters, and public contracts as those layers become necessary. `backend/src/Hosts` contains executable composition roots.

The first modules are Portfolio, Knowledge, Assistant, JobMatching, and Contact. Hosts may compose modules; modules must never depend on a host.

The dependency direction inside a module is:

```text
Host transport -> Application -> Domain
                     ^
                     |
              Infrastructure
```

- `Domain/` contains business concepts and deterministic rules.
- `Application/` defines use cases and ports consumed by hosts.
- `Infrastructure/` implements application ports for storage or providers.
- Hosts own HTTP, SSE, scheduling, serialization, and dependency composition.

Only create a layer directory when it contains real code. Empty architecture
placeholders are not part of the design.

## Frontend boundaries

`frontend/src/app` composes routes and layouts. Product behavior belongs in
`frontend/src/modules/<feature>/`; reusable UI or utilities move to `shared/`
only after at least two modules need them. Frontend modules consume public
backend contracts and never access backend persistence or cloud providers.

## Repository shape

```text
frontend/src/app/                 route composition
frontend/src/modules/             frontend feature modules
backend/src/BuildingBlocks/       minimal cross-module primitives
backend/src/Modules/              business capabilities
backend/src/Hosts/                executable composition and transports
backend/tests/Unit/               domain and use-case tests
backend/tests/Architecture/       dependency rules
docs/adr/                         accepted architecture decisions
docs/runbooks/                    repeatable engineering workflows
```

## Evidence rule

The UI and the agent may only present professional claims backed by the canonical content set. Until a project is connected to verified content, the API reports its evidence status as `pending`.

The initial in-memory catalog is scaffolding, not verified professional
evidence or a persistence decision.

## Data lifecycle

The canonical [data handling policy](data-handling.md) defines which values are
durable, transient, sensitive, or forbidden from application persistence.
Persistence is deny-by-default: public evidence is versioned, session content
is ephemeral, and every new store must declare retention and deletion behavior.
