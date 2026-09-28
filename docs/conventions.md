# Engineering conventions

These rules keep generated changes small enough to review and correct.

## General

- Use English in repository text, code, tests, commits, and public artifacts.
- Prefer one thin behavior over framework or abstraction expansion.
- Do not add a dependency, deployable, shared abstraction, or persistence layer
  until the current behavior requires it.
- Treat generated code as untrusted until its contracts, tests, and diff have
  been reviewed.
- Keep public claims tied to versioned evidence. Use `pending` when evidence is
  not connected.

## Backend

- Use `Rafael.Portfolio.<Area>` namespaces and one public type per file.
- Keep domain types free of ASP.NET, cloud SDK, storage, and model-provider
  dependencies.
- Put use-case interfaces in `Application/` and adapters in `Infrastructure/`.
- Keep endpoint mapping and dependency registration in a host.
- Modules may depend on BuildingBlocks and explicit module contracts; modules
  never reference hosts or another module's internals.
- Add unit tests for business behavior and architecture tests for dependency
  rules that must remain true.

## Frontend

- Keep route files focused on composition.
- Put feature data, models, components, and API clients under
  `src/modules/<feature>/`.
- Use explicit TypeScript types at API and module boundaries; avoid `any`.
- Keep server/provider credentials and backend persistence out of the frontend.
- Create shared code only when two real consumers exist.

## Infrastructure as code

- Coding models may author Terraform and deployment definitions when the task
  names the environment, provider boundary, allowed paths, and acceptance checks.
- Keep environments, identities, remote state, secrets, and provider versions
  explicit; never embed credentials or secret values.
- Treat generated plans as review input, not permission to apply them.
- Only the human owner and Codex run commands that access provider state or
  credentials, and only with explicit authorization.

## Change size

A generated task should normally change one behavior, its tests, and its
documentation or contract when required. If a task changes architecture and
product behavior together, split it unless the contract cannot be demonstrated
otherwise.
