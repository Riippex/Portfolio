# Evidence Record: JobTY

- Identifier: `evidence-project-jobty`
- Slug: `jobty`
- Kind: Project
- Status: `verified`
- Version: `2026.11.1`
- Last reviewed: 2026-11-05
- Canonical Source: https://github.com/Riippex/JobTY/tree/bd85d00826944bebb10dfbe95a702a08d8e84bb1

## Matching engine

JobTY uses an LLM to evaluate a vacancy against a parsed candidate profile and returns a structured fit score, reasons, concerns, and a recommendation.

The scorer uses the job description and saved candidate profile as inputs, validates the response shape, and can reuse a score stored for the profile and job. The score expresses the model's assessment.

[Job scoring implementation](https://github.com/Riippex/JobTY/blob/bd85d00826944bebb10dfbe95a702a08d8e84bb1/back/app/services/job_scorer.py)

## PDF parsing

The backend extracts text from a PDF résumé and passes it to a configured LLM to produce a structured candidate profile.

The parser reads PDF pages with pdfplumber and uses a typed result for the extracted candidate data.

[PDF parsing implementation](https://github.com/Riippex/JobTY/blob/bd85d00826944bebb10dfbe95a702a08d8e84bb1/back/app/services/cv_parser.py)

## Application architecture

The development snapshot defines a Next.js frontend and a FastAPI backend in Docker Compose, with SQLite configured as the default database.

The service definitions show the local application layout and the configuration boundary for the backend's model provider.

[Docker Compose service definitions](https://github.com/Riippex/JobTY/blob/bd85d00826944bebb10dfbe95a702a08d8e84bb1/docker-compose.yml)
