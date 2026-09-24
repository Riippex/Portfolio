---
name: portfolio-delivery
description: Prepare Portfolio changes for a validated direct develop commit or a task pull request, including scope, private-file, generated-file, and proportional verification checks.
---

# Portfolio delivery

Read `docs/runbooks/pull-requests.md` and inspect the working tree before staging.

During active construction, validated task-scoped commits may go directly to `develop`. Use a short-lived branch and pull request when isolation, review, parallel work, or risk makes that useful. Reuse an existing task branch and PR for follow-ups.

Before committing or publishing:

1. Freeze the intended paths and exclude unrelated changes.
2. Run validation proportional to frontend, backend, agent, contract, documentation, or deployment impact.
3. Synchronize project skills when `.agents/skills/` changes and verify mirrors with `pwsh tools/sync-agent-skills.ps1 -Check`.
4. Inspect staged content for `documents/`, local instructions, `.codex/`, `.gemini/`, local graph indexes, résumés, credentials, environment files, cloud state, and build output.
5. Inspect `git diff --cached --check` and the staged diff; use a focused Conventional Commit.

Push or open a PR only when requested or already authorized by the selected workflow. Never update or promote `main`, deploy, merge, approve, close, retarget, bypass checks, or modify credentials without explicit authorization. Report the actual destination, commit or PR, checks, dirty-tree state, and limitations.
