export function canonicalizeIp(rawIp: string): string {
  if (!rawIp) return "";
  let trimmed = rawIp.trim();
  if (!trimmed) return "";

  if (trimmed.startsWith("[") && trimmed.includes("]")) {
    const endBracket = trimmed.indexOf("]");
    trimmed = trimmed.substring(1, endBracket);
  } else if (trimmed.includes(":") && trimmed.indexOf(":") === trimmed.lastIndexOf(":")) {
    trimmed = trimmed.split(":")[0];
  }

  const isIPv4 = /^(\d{1,3}\.){3}\d{1,3}$/.test(trimmed);
  if (isIPv4) {
    const parts = trimmed.split(".").map((p) => parseInt(p, 10));
    if (parts.every((p) => p >= 0 && p <= 255)) {
      return parts.join(".");
    }
  }

  const isIPv6 = /^[0-9a-fA-F:]+$/.test(trimmed) && trimmed.includes(":");
  if (isIPv6) {
    return trimmed.toLowerCase();
  }

  return trimmed;
}

export function parseTeamAllowlist(rawSecret: string | undefined): { valid: boolean; ips: string[] } {
  if (!rawSecret || !rawSecret.trim()) {
    return { valid: true, ips: [] };
  }

  const trimmed = rawSecret.trim();
  let entries: string[] = [];

  if (trimmed.startsWith("[")) {
    try {
      const parsed = JSON.parse(trimmed);
      if (!Array.isArray(parsed)) {
        return { valid: false, ips: [] };
      }
      entries = parsed.map((e) => String(e));
    } catch {
      return { valid: false, ips: [] };
    }
  } else {
    entries = trimmed.split(/[\s,]+/).filter(Boolean);
  }

  if (entries.length > 64) {
    return { valid: false, ips: [] };
  }

  const canonicalIps: string[] = [];
  for (const entry of entries) {
    const canonical = canonicalizeIp(entry);
    if (!canonical) {
      return { valid: false, ips: [] };
    }
    canonicalIps.push(canonical);
  }

  return { valid: true, ips: canonicalIps };
}

export function isTeamMember(visitorIp: string | null | undefined, allowlist: string[]): boolean {
  if (!visitorIp || !allowlist || allowlist.length === 0) {
    return false;
  }
  const canonical = canonicalizeIp(visitorIp);
  if (!canonical) return false;
  return allowlist.includes(canonical);
}
