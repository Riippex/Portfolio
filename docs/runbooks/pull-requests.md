# Delivery workflow

## Branch model

`develop` is the integration branch during active construction. Validated, task-scoped commits may be pushed directly when shared review is unnecessary. Use a short-lived `codex/`, `feat/`, `fix/`, `docs/`, `refactor/`, `test/`, `ci/`, or `chore/` branch and a pull request to `develop` when isolation, review, parallel work, or higher risk makes that safer.

`main` is the release boundary. Updating `main`, merging or promoting `develop` into it, and publishing a release or deployment require explicit owner authorization.

## Agent commit boundary

For an authorized roadmap item, Gemini may create one focused commit after the
item acceptance checks pass. After Codex freezes and reviews that commit, either
Kimi or Claude may create one focused follow-up commit for the assigned
correction block. Do not amend or rewrite earlier handoff commits. These commit
permissions never imply permission to push, open or change a pull request,
deploy, apply infrastructure, or update `main`.

## Required close-out

1. Inspect the working tree before and after the task; preserve unrelated changes.
2. Validate proportionally to the affected paths:
   - frontend: `npm run lint`, `npm run build`, and `npm run build:vinext` when Cloudflare compatibility matters;
   - backend: `dotnet build backend/Rafael.Portfolio.sln` and `dotnet test backend/Rafael.Portfolio.sln`;
   - agent workflow: `pwsh tools/sync-agent-skills.ps1 -Check` plus skill validation when a skill changes;
   - infrastructure: format, validate, plan, and cost/security review without applying unless explicitly authorized.
3. Check generated files and confirm no secret, credential, private note, local assistant state, raw résumé, build output, or machine-only file is staged.
4. Stage an explicit task scope and inspect `git diff --cached` before committing.
5. Use a focused Conventional Commit.
6. Push only when the user requested it or the selected task workflow already authorizes remote delivery. Never change credentials to overcome an authentication failure.
7. For a task branch, open or update one non-draft pull request targeting `develop`, complete the repository template, and report its URL and actual check state. Do not merge, approve, close, or retarget it without explicit authorization.

## Handoff

Report the destination branch, commit or PR when one exists, exact validation performed, dirty-tree state, and any known limitation. Planned checks are not completed checks, and a successful build is not a deployment.
