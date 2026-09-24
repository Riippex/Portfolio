# Graph tool routing

| Task | Preferred tool |
|---|---|
| Symbols, callers, imports, and routine change impact | codegraph |
| Current diff impact and custom graph queries | GitNexus |
| Public documentation, contracts, evidence, and implementation relationships | graphify |

These tools are optional navigation accelerators. Check the installed version and
help, repository root, and index freshness before trusting their output. If a
tool is unavailable, use `rg` and direct source inspection and report the
limitation.

## Repository scope

Initialize codegraph and GitNexus once at the repository root. A root index can
connect the TypeScript frontend, .NET backend, public contracts, and deployment
definitions. Verify cross-runtime and deployment claims against their source
contracts and configuration because an inferred graph edge does not prove
runtime behavior, authorization, evidence quality, or ownership.

Do not index another repository into the Portfolio index. When an investigation
crosses repositories, query each repository separately and verify the boundary
through an explicit contract or source reference.

## Index lifecycle

- **codegraph:** run `codegraph status` before use. Run `codegraph init` for a
  fresh clone, `codegraph sync` after normal changes, and a full rebuild after a
  large refactor or branch switch when status shows drift. Do not assume an MCP
  watcher is active.
- **GitNexus:** run `gitnexus status` before use and
  `gitnexus analyze --index-only --skip-agents-md` after relevant source changes,
  pulls, or branch switches. `--index-only` prevents generated agent instructions
  and skills from rewriting the repository. Keep embeddings disabled unless the
  owner explicitly authorizes their resource and data implications.
- **graphify:** use it on demand for architecture, evidence, contract, and
  documentation audits. Treat `graphify-out/` as a point-in-time local result
  and regenerate it before relying on it.

Inspect `git status` immediately after any initialization or rebuild. Do not
keep tool-generated instruction files, metadata, or package changes.

## Privacy and publication boundary

Local indexes under `.codegraph/` and `.gitnexus/`, plus `graphify-out/`, stay
ignored and must not be committed. Never include `documents/`, local instruction
files, `.agents/`, `.claude/`, `.codex/`, `.gemini/`, credentials, environment
files, build output, raw resumes, or private evidence in an index or graphify
input.

Git ignore rules are publication controls, not a universal access boundary.
Before a graphify run, stage only reviewed public source, contracts, evidence,
and documentation as inputs. Do not disable ignore parsing or enable remote
uploads or embeddings merely to answer a navigation question.
