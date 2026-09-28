# AI development loop

The repository, tests, and accepted decisions are the source of truth. Model
roles organize work; they do not grant extra permissions.

## Roles

1. **Human owner:** chooses the product slice, evidence, priorities, and any
   architecture or deployment decision. The owner and Codex operate the local
   or cloud console; provider access always remains under owner authorization.
2. **Gemini — implementer:** reads `AGENTS.md`, architecture, conventions, the
   affected contract, and nested instructions. It implements one bounded slice,
   adds proportional tests, runs the required checks, and creates one focused
   Conventional Commit when the roadmap item is complete.
3. **Codex — reviewer and integrator:** inspects the diff and source, checks
   the frozen implementation commit, boundaries, evidence, security, tests, and
   generated files, then either accepts the block or produces concrete findings.
   It performs final validation and delivery only when authorized.
4. **Kimi or Claude — corrector:** receives one bounded block of reproduced
   findings, makes the smallest corrections, runs the required checks, and
   creates one focused follow-up commit. Choose one according to available
   tokens; do not run both on the same correction block by default.
5. **Codex — verification:** reruns affected checks and confirms whether the
   finding is resolved. A correction claim without a passing check remains
   unverified.

## Commit and review blocks

Before implementation, record the item base commit. A completed Gemini item is
one reviewable commit on `develop`; it is not amended after handoff. Codex
freezes the base, head, paths, checks, limitations, and working-tree state, then
reviews only that block.

If findings require correction, assign the bounded finding set to either Kimi
or Claude. The corrector adds one follow-up commit without rewriting Gemini's
commit. Codex reviews that correction commit and verifies the complete range
from the original base through the correction head. Further correction rounds
repeat the same pattern, preserving commit history.

Implementation and correction agents may commit and push an authorized roadmap
item or correction block directly to `origin/develop` after its required checks
pass. Before pushing, they confirm the remote still descends from the recorded
base; on divergence they stop rather than force-push or rewrite history. They
never open or mutate pull requests, deploy, apply infrastructure, or update
`main` unless the owner explicitly authorizes that separate action.

## Infrastructure workflow

Gemini, Kimi, and Claude may generate or correct Terraform and related
deployment definitions within an explicitly bounded task. Infrastructure code
receives the same review, testing, and correction loop as application code.

Codex reviews provider boundaries, IAM, state handling, security, cost, and the
resulting plan with the human owner. Commands that access provider state or
credentials, plus `apply`, deployment, DNS, billing, and secret operations, are
performed only through the owner-and-Codex console workflow with explicit
authorization. Generated infrastructure is proposed code; it is not evidence
that a resource exists or that a deployment succeeded.

## Task brief

Every implementation or correction handoff must include:

- user-visible behavior and owning module;
- allowed paths and explicit out-of-scope work;
- dependency direction and trust boundary;
- data classification, retention, deletion, and logging behavior;
- acceptance criteria and commands to run;
- relevant contracts, evidence, and known gaps;
- current branch and whether commit, push, PR, or deployment is authorized.
- base commit and expected commit boundary for the handoff.

## Output contract

Each model reports:

- files changed and why;
- behavior implemented versus still planned;
- checks actually run and their results;
- assumptions, risks, and unresolved decisions;
- no claim of deployment, verification, or evidence without proof.

Review findings should identify a file and concrete failure mode. Correction
prompts should contain only the relevant finding, evidence, allowed scope, and
verification command so token availability does not change the intended fix.
