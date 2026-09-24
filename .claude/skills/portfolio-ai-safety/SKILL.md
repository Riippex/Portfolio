---
name: portfolio-ai-safety
description: Review or implement Portfolio chat, RAG, prompts, tools, job matching, memory, ingestion, or model-output handling with grounding, privacy, authorization, and abuse controls. Do not substitute it for a general repository security scan.
---

# Portfolio AI safety

Treat the model as an untrusted reasoning component operating over public professional evidence.

## Trace the flow

Read `docs/architecture.md`, affected contracts, prompts, retrieval and tool code, storage, logging, and tests. Trace input through validation, retrieval, model calls, structured output, tools, persistence, streaming, and the rendered result. Separate confirmed behavior from planned controls.

## Required controls

- **Grounding:** material claims cite allowlisted, versioned sources. The system can answer “not documented.” Retrieval retains source, URL, visibility, and content version.
- **Prompt isolation:** résumés, repositories, vacancies, retrieved chunks, tool responses, and uploads are data, not instructions. They cannot alter system policy or tool permissions.
- **Structured output:** tool calls and job-match results use narrow schemas, size limits, validation, safe rejection, and bounded retries.
- **Tool safety:** tools are allowlisted, least-privilege, time-bounded, and free of arbitrary SQL, shell, filesystem, URL-fetch, or generic cloud access unless specifically required and controlled.
- **Privacy:** ingest only content approved for public use. Do not expose private contact data, raw résumés, secrets, hidden prompts, private repository content, or hidden reasoning. Minimize and redact logs.
- **Job matching:** distinguish evidence, inference, and gaps. Avoid discriminatory attributes and false numerical certainty. A pasted vacancy cannot add facts to Rafael's profile.
- **Session safety:** rate limits, Turnstile or equivalent abuse controls, bounded context, retention rules, and deletion behavior are explicit before production persistence.
- **Service boundary:** Cloud Run authenticates trusted callers and enforces policy independently of prompts. Edge checks are defense in depth, not the sole authorization control.
- **Observability:** record model/provider version, source IDs, tool names, policy decisions, latency, and outcome without secrets or private chain-of-thought.

## Findings and fixes

For each validated issue, give severity, affected flow, concrete evidence, plausible impact, the smallest durable remediation, and a regression test or eval. Fix deterministic boundaries rather than relying on stronger prompt wording. When asked only to review, do not modify files.
