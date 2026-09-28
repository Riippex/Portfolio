# ADR 0001: Modular monolith and feature modules

- Status: Accepted
- Date: 2026-09-28

## Context

The portfolio needs a small base that multiple coding models can extend without
inventing new boundaries. It has a Next.js frontend, a .NET backend, and future
grounded AI behavior, but does not yet need independently deployed services.

## Decision

Use a .NET modular monolith with capability modules under
`backend/src/Modules/` and executable composition roots under
`backend/src/Hosts/`. Inside a module, dependencies point from infrastructure
and host adapters toward application ports and domain concepts.

Organize the Next.js application by feature under `frontend/src/modules/`, with
`frontend/src/app/` limited to route and layout composition. Cross-runtime
integration uses explicit HTTP, SSE, OpenAPI, or event contracts.

Do not create a root service, shared abstraction, or persistence layer until a
real behavior demonstrates an independent lifecycle or a second consumer.

## Consequences

- A generator has clear locations and dependency direction.
- Hosts can change transport without moving business policy out of modules.
- Architecture tests can reject module-to-host dependencies.
- Some early code remains intentionally in memory until persistence is needed.
- A future service extraction requires a superseding ADR and explicit contract.
