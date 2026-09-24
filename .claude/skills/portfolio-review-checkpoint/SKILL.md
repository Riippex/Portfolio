---
name: portfolio-review-checkpoint
description: Freeze a completed Portfolio implementation or correction block for a separate, bounded code review. Use when review is requested or a handoff between agents is needed; skip ordinary status updates.
---

# Portfolio review checkpoint

Create an unambiguous review scope without launching or delegating a reviewer unless the user asks.

Before the handoff, record the block's base commit, head commit when committed, included commits, changed paths, validation results, known limitations, and dirty-tree state. Confirm unrelated work is excluded. For uncommitted work, state that clearly and list the exact paths.

The reviewer must inspect `git status --short --untracked-files=all` and review only the frozen range or paths. Do not substitute `origin/develop` when the recorded base differs. If base or scope is ambiguous, ask rather than reviewing a cumulative branch diff.

Review findings are ordered by severity with concrete file references. Distinguish introduced defects from pre-existing debt and state whether the block is ready to integrate. Run adversarial or security review only when explicitly requested or when a matching security skill genuinely applies.

The checkpoint does not authorize commits, pushes, PR mutations, merges, deployments, or releases.
