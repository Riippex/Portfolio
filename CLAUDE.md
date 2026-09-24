# Claude Code workflow

Read and follow [AGENTS.md](AGENTS.md), [architecture](docs/architecture.md), and the [delivery workflow](docs/runbooks/pull-requests.md).

Project skills are mirrored under `.claude/skills/` from `.agents/skills/`. Read only matching skills. `CLAUDE.local.md` contains optional ignored machine or private context. Public documentation belongs in `docs/`; private working material belongs in ignored `documents/`.

After an implementation block is ready for a separate review, use `portfolio-review-checkpoint` to freeze its exact scope. Do not infer permission to push, open a pull request, deploy, or promote `main` from a skill.
