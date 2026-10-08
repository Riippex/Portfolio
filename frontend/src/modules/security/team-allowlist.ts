import { canonicalizeIp } from "./ip";

export const MAX_ALLOWLIST_ENTRIES = 64;
const MAX_ALLOWLIST_CHARS = 8192;

export type TeamAllowlist =
  | { readonly status: "absent" }
  | { readonly status: "invalid" }
  | { readonly status: "ok"; readonly ips: readonly string[] };

/**
 * Parses the per-environment TEAM_ALLOWLIST secret: a JSON array of at most 64 exact IP
 * strings. Anything else is "invalid" and never partially accepted: comma lists, objects,
 * non-strings, CIDRs, ports, wildcards, whitespace and malformed addresses all invalidate the
 * whole value. Equivalent IPv6 spellings are normalised to one canonical form.
 */
export function parseTeamAllowlist(raw: unknown): TeamAllowlist {
  if (raw === undefined || raw === null || raw === "") return { status: "absent" };
  if (typeof raw !== "string" || raw.length > MAX_ALLOWLIST_CHARS) return { status: "invalid" };

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return { status: "invalid" };
  }

  if (!Array.isArray(parsed) || parsed.length > MAX_ALLOWLIST_ENTRIES) return { status: "invalid" };

  const ips = new Set<string>();
  for (const entry of parsed) {
    const canonical = canonicalizeIp(entry);
    if (canonical === null) return { status: "invalid" };
    ips.add(canonical);
  }

  return { status: "ok", ips: [...ips] };
}

export function isTeamMember(visitorIp: string, allowlist: readonly string[]): boolean {
  const canonical = canonicalizeIp(visitorIp);
  return canonical !== null && allowlist.includes(canonical);
}
