# AI development loop

The repository, tests, and accepted decisions are the source of truth. Model
roles organize work; they do not grant extra permissions.

## Roles

1. **Human owner:** chooses the product slice, evidence, priorities, and any
   architecture or deployment decision.
2. **Gemini — implementer:** reads `AGENTS.md`, architecture, conventions, the
   affected contract, and nested instructions. It implements one bounded slice,
   adds proportional tests, and reports the exact checks it ran.
3. **Codex — reviewer and integrator:** inspects the diff and source, checks
   boundaries, evidence, security, tests, and generated files, then either
   accepts the slice or produces concrete findings. It performs final validation
   and delivery only when authorized.
4. **Kimi or Claude — corrector:** receives the failing check or review finding,
   reproduces it, and makes the smallest correction. Choose one according to
   available tokens; do not run both on the same correction by default.
5. **Codex — verification:** reruns affected checks and confirms whether the
   finding is resolved. A correction claim without a passing check remains
   unverified.

## Task brief

Every implementation or correction handoff must include:

- user-visible behavior and owning module;
- allowed paths and explicit out-of-scope work;
- dependency direction and trust boundary;
- acceptance criteria and commands to run;
- relevant contracts, evidence, and known gaps;
- current branch and whether commit, push, PR, or deployment is authorized.

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
