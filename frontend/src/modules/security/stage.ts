// The deployment stage is one explicit server-side value, PORTFOLIO_STAGE. It is written to
// each Worker by the build (wrangler.jsonc "vars" per environment) and is never inferred from
// NODE_ENV, the hostname or any request value. A missing or unrecognised value is not
// defaulted: callers fail closed.
//
// "local" exists only for developing on a workstation (next dev, vitest). Deployed Workers are
// built with "dev" or "prod", which tools/check-wrangler-build.mjs enforces.

export const STAGES = ["local", "dev", "prod"] as const;
export type Stage = (typeof STAGES)[number];

export function parseStage(value: unknown): Stage | null {
  return typeof value === "string" && (STAGES as readonly string[]).includes(value)
    ? (value as Stage)
    : null;
}
