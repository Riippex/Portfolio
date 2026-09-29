# Canonical public evidence inventory

This directory owns the versioned, public evidence that backs all professional
claims presented on the portfolio home page, project detail views, and by the
AI assistant.

## Ownership and governance

1. **Owner authority:** Rafael Patiño is the author and approving authority for all
   public evidence entries.
2. **Deny-by-default claims:** If a project or capability does not have a verified
   public source URL, demonstration, or inspectable artifact, its `evidenceStatus`
   must remain `pending`.
3. **Reference separation:** Design references (such as private codebases or prior
   employment systems) must never be presented as public claims or evidence
   without explicit verification.

## Manifest specification (`inventory.json`)

The inventory manifest (`inventory.json`) is the machine-readable catalog consumed
by the Knowledge module and automated verification tests.

Each entry conforms to the following schema:

- `id`: unique identifier (`evidence-profile-001`, `evidence-project-<slug>`)
- `slug`: matching project or topic slug
- `kind`: `profile` | `project`
- `title`: human-readable title
- `summary`: factual synopsis
- `version`: evidence revision (e.g. `2026.09`)
- `evidenceStatus`: `pending` | `verified`
- `sourceUrl`: public repository or documentation URL (nullable for pending)
- `lastReviewed`: ISO 8601 date of verification or review
- `headline`: professional headline (required for `profile` items)
- `focusAreas`: non-empty list of focus areas (required for `profile` items)
- `claims`: array of atomic claims:
  - `claimId`: unique claim identifier (e.g. `claim-vextis-01`)
  - `statement`: verifiable factual statement
  - `status`: `pending` | `verified`
  - `citation`: source document, line, or public artifact reference

## Invalidation and updates

Modifications and revocations are versioned commits in this repository. When an
evidence item changes or is removed:
1. Update `inventory.json` and the corresponding Markdown file.
2. The Knowledge module and indexer invalidate any associated retrieval caches
   and index records.
