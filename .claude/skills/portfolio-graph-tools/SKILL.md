---
name: portfolio-graph-tools
description: Choose graph tools for Portfolio cross-file navigation, diff impact, or public documentation-to-code audits. Skip isolated edits and use direct source inspection when a graph adds no value.
---

# Portfolio graph tools

Read `docs/runbooks/graph-tools.md` before using or rebuilding a graph.

Use codegraph for symbol, caller, import, and routine impact navigation. Use
GitNexus for current-diff impact and custom graph queries. Use graphify for
relationships between reviewed public documentation, contracts, evidence, and
implementation.

Confirm tool availability, repository root, index freshness, and exclusions
before trusting results. Keep the Portfolio index separate from other
repositories and trace cross-runtime boundaries through explicit contracts.
Graphs are navigation evidence, not proof of runtime behavior, authorization,
ownership, or professional evidence.

Never index or export `documents/`, local instructions, assistant state,
credentials, environment files, raw resumes, private evidence, or build output.
Never disable ignore parsing or enable remote uploads or embeddings without
explicit authorization. Inspect the working tree after index generation. If a
tool is unavailable, fall back to `rg` and direct source reads and report the
limitation.
