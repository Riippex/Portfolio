# Agent workflow

- Repository text, code comments, commits, and public artifacts are English; conversation may be Spanish.
- This repository owns the Next.js frontend, the .NET modular monolith, portfolio evidence, and Cloudflare/GCP deployment definitions. Reusable products such as the Rust agent harness belong in separate repositories. Create a root `services/` directory only for independently deployed processes.
- Read [architecture](docs/architecture.md), accepted ADRs, affected contracts, and any nested `AGENTS.md` before changing boundaries. The generated rules in `frontend/AGENTS.md` apply inside the frontend.
- Follow the [data handling policy](docs/data-handling.md) before adding persistence, caches, indexes, events, analytics, logs, uploads, contact handling, chat memory, or model context retention.
- Follow the [delivery workflow](docs/runbooks/pull-requests.md). Validated direct commits to `develop` are allowed during active construction; use a short-lived branch and pull request when isolation or review is useful. Never update or promote `main` without explicit authorization.
- Use [agent skill routing](docs/runbooks/agent-skills.md) when a task matches a local skill. Canonical project skills live in `.agents/skills/`; `.claude/skills/` contains synchronized mirrors.
- Follow the [AI development loop](docs/runbooks/ai-development.md) when implementation, review, and correction move between models. Every handoff must preserve scope, acceptance criteria, and actual check results.
- Use [graph tool routing](docs/runbooks/graph-tools.md) for cross-file navigation, diff impact, and documentation-to-code audits. Graph indexes are local accelerators, never sources of truth or committed artifacts.
- Public material belongs in `docs/`. Private notes belong in ignored `documents/` or `AGENTS.local.md`; never publish them.
- Preserve unrelated changes. Distinguish planned, implemented, and verified behavior. Do not claim professional experience or project outcomes without versioned evidence.
- Do not spawn subagents unless the user requests delegation or parallel agent work.
