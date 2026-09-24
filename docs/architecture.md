# Architecture

The portfolio is a product demonstration, not a static résumé. Its public claims must be traceable to versioned evidence.

## Deployables

- `frontend/`: Next.js application deployed to Cloudflare Workers.
- `backend/`: .NET 10 modular monolith deployed to Google Cloud Run.
- `services/`: created only when a process needs an independent runtime, release, or scaling model.

## Backend boundaries

`backend/src/Modules` contains business capabilities. Each module owns its domain, use cases, infrastructure adapters, and public contracts as those layers become necessary. `backend/src/Hosts` contains executable composition roots.

The first modules are Portfolio, Knowledge, Assistant, JobMatching, and Contact. Hosts may compose modules; modules must never depend on a host.

## Evidence rule

The UI and the agent may only present professional claims backed by the canonical content set. Until a project is connected to verified content, the API reports its evidence status as `pending`.
