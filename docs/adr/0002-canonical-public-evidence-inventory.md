# ADR 0002: Canonical public evidence inventory and source ownership

- Status: Accepted
- Date: 2026-09-28

## Context

The portfolio and its assistant must only present claims backed by versioned, public evidence (the evidence rule in `docs/architecture.md` and `docs/data-handling.md`). Unverified claims must remain explicitly `pending`. Before implementing detail pages (P-003) or automated retrieval ingestion (K-001), the repository needs a canonical source location, clear ownership rules, a versioned schema, and an invalidation procedure for public evidence.

## Decision

1. Store canonical public evidence in `docs/evidence/` as versioned Markdown source documents and a machine-readable `inventory.json` manifest.
2. The repository owner (Rafael Patiño) is the sole authority for introducing, modifying, or verifying public evidence.
3. Every evidence item defines an identifier, slug, title, version, evidence status (`pending` or `verified`), summary, structured claims, source URLs, and review timestamp.
4. Claims without public, externally inspectable artifacts (e.g. public repositories, published articles, live deployments) must retain status `pending`.
5. Design references (e.g. private systems or past employers) inform architectural patterns but are strictly separated and never converted into public evidence without explicit authorization and public proof.
6. Invalidation and removal follows `docs/data-handling.md`: removal or modification is a versioned change committed to the repository, followed by downstream index invalidation.

## Consequences

- The frontend, backend Knowledge module, and Assistant retrieval have a single, version-controlled source of truth.
- Evidence can be validated offline and in automated unit tests.
- Claim citations in project pages and AI responses reference deterministic claim IDs and canonical source URLs.
- Transition from `pending` to `verified` requires a versioned update to the manifest and source documents.
