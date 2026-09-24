# Agent skill routing

Canonical skills live in `.agents/skills/`. Byte-identical copies under `.claude/skills/` support Claude Code. Edit the canonical skill, then run `pwsh tools/sync-agent-skills.ps1`. Use `-Check` in validation and CI-style checks.

| Skill | Use when |
|---|---|
| `portfolio-architecture` | Module, API, evidence ownership, data, runtime, or deployment boundaries change |
| `portfolio-product-slice` | Implementing one user-visible behavior across frontend, backend, contracts, or persistence |
| `portfolio-ai-safety` | Reviewing or changing chat, RAG, prompts, tools, job matching, memory, or model output handling |
| `portfolio-cloud-review` | Cloudflare/GCP infrastructure, IAM, deployment, observability, or cost changes |
| `portfolio-delivery` | Preparing a direct `develop` commit or a task pull request |
| `portfolio-review-checkpoint` | Freezing an implementation block for a separate, bounded code review |

Read only skills that match the task. Skills guide implementation but do not grant permission to push, deploy, alter cloud resources, merge, or release.
