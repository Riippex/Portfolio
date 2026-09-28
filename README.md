# Rafael Portfolio

An interactive portfolio for an AI systems engineer. The finished product will combine selected work with a grounded assistant that can explain projects, cite evidence, and evaluate job descriptions without inventing experience.

## Repository

```text
frontend/    Next.js + TypeScript on Cloudflare Workers
backend/     .NET 10 modular monolith on Google Cloud Run
deployment/ Cloudflare and GCP infrastructure
docs/       Architecture and decision records
```

Independent deployables will live in a root `services/` directory when the first real service exists.

## Local development

```bash
cd frontend
npm install
npm run dev
```

```bash
cd backend
dotnet restore
dotnet run --project src/Hosts/Web/Rafael.Portfolio.Web
```

The backend exposes `/health`, `/v1/profile`, `/v1/projects`, and OpenAPI during development.

## Verification

```bash
cd frontend
npm run lint
npm run build
npm run build:vinext
```

```bash
cd backend
dotnet test
```

See [docs/architecture.md](docs/architecture.md) for module boundaries and the evidence policy.
Repository conventions are defined in [docs/conventions.md](docs/conventions.md).

## AI-assisted development

Repository-wide agent rules live in [AGENTS.md](AGENTS.md). Project skills are
versioned under `.agents/skills/`, mirrored for Claude under `.claude/skills/`,
and routed by [docs/runbooks/agent-skills.md](docs/runbooks/agent-skills.md).

After editing a canonical skill:

```powershell
pwsh tools/sync-agent-skills.ps1
pwsh tools/sync-agent-skills.ps1 -Check
```

For cross-file navigation and impact analysis, initialize the optional local
graph indexes from the repository root:

```powershell
codegraph init
gitnexus analyze --index-only --skip-agents-md
```

Use [graph tool routing](docs/runbooks/graph-tools.md) to select the right tool
and keep private material out of generated indexes and audits.

Delivery and branch rules are documented in
[docs/runbooks/pull-requests.md](docs/runbooks/pull-requests.md).
The multi-model implementation and review loop is documented in
[docs/runbooks/ai-development.md](docs/runbooks/ai-development.md).
Gemini reads the shared repository rules through the small compatibility entry points in `GEMINI.md` and `frontend/GEMINI.md`.
