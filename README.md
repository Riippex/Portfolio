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
