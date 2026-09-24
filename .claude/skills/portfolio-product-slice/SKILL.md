---
name: portfolio-product-slice
description: Implement one thin, demonstrable Portfolio behavior across Next.js, .NET modules, contracts, evidence, tests, and observability. Use for end-to-end features; skip broad architecture reviews and unrelated maintenance.
---

# Portfolio product slice

Deliver the smallest complete user outcome without placeholder layers or unsupported claims.

## Define the slice

Read `docs/architecture.md`, the affected module, public contract, frontend flow, and existing tests. Express the outcome with:

- visitor or recruiter intent;
- input and observable result;
- owning backend module;
- required public evidence and citations;
- model autonomy, if any;
- privacy, abuse, and failure behavior.

Keep one primary path and only the failure paths needed for safety, grounding, or a credible experience.

## Implement contract first

1. Define or change the minimum HTTP, SSE, or structured-output contract when crossing a boundary.
2. Implement backend rules and use cases before provider or transport adapters.
3. Add retrieval or model orchestration only when deterministic code cannot produce the outcome. Validate model output against a strict schema.
4. Build the smallest accessible Next.js flow that exposes loading, success, missing-evidence, and actionable failure states.
5. Add infrastructure only for resources the slice exercises.

The frontend must not invent professional data, write backend stores directly, or rely on hidden model behavior for authorization. Job-match scores must expose evidence and gaps rather than presenting unsupported precision.

## Verify and report

Cover applicable unit, contract, integration, architecture, component, browser, and agent-evaluation checks. Run narrow checks first, then broader checks justified by the paths changed. Say when a boundary remains mocked.

Report the working outcome, modules and contracts changed, exact verification, mocked or deferred components, and the next thinnest slice without implementing it unless requested.
