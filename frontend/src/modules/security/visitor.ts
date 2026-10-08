import { canonicalizeIp } from "./ip";

export interface PlatformVisitor {
  readonly ip: string;
  readonly country: string;
}

export const UNKNOWN_COUNTRY = "XX";

// ISO 3166-1 alpha-2, plus the Cloudflare pseudo-codes XX (unknown) and T1 (Tor).
const COUNTRY_PATTERN = /^[A-Z][A-Z0-9]$/;

export function normalizeCountry(value: unknown): string {
  if (typeof value !== "string") return UNKNOWN_COUNTRY;
  const upper = value.toUpperCase();
  return COUNTRY_PATTERN.test(upper) ? upper : UNKNOWN_COUNTRY;
}

/**
 * Reads the visitor from metadata asserted by the Cloudflare platform only: CF-Connecting-IP
 * and the runtime's request.cf.country (or the CF-IPCountry header the Worker entry writes
 * from it). It never consults X-Forwarded-For, Forwarded, X-Real-IP, True-Client-IP or any
 * other header a caller can set, so a missing or malformed platform address yields null
 * instead of a fallback.
 */
export function readPlatformVisitor(request: Request): PlatformVisitor | null {
  const ip = canonicalizeIp(request.headers.get("CF-Connecting-IP"));
  if (ip === null) return null;

  const cf = (request as Request & { cf?: { country?: unknown } }).cf;
  const country = normalizeCountry(cf?.country ?? request.headers.get("CF-IPCountry"));
  return { ip, country };
}
