import { canonicalizeIp } from "./ip";
import { parseStage, type Stage } from "./stage";
import { isTeamMember, parseTeamAllowlist } from "./team-allowlist";
import { normalizeCountry, readPlatformVisitor } from "./visitor";

// Signed visitor identity, version 2. The backend implements the same contract
// (SignedIdentity.cs); docs/contracts/visitor-identity-v2.json holds vectors both sides test.
//
//   X-Portfolio-Identity       = base64url(UTF-8 JSON claims), no padding
//   X-Portfolio-Identity-Proof = lowercase hex HMAC-SHA256(secret, "portfolio-identity-v2\n" +
//                                the exact X-Portfolio-Identity value as transmitted)
//
// The proof covers the transmitted bytes, so nothing is re-serialised before verification,
// and the claims bind the stage, HTTP method and request path so a captured identity cannot
// be replayed against another stage or endpoint.

export const IDENTITY_HEADER = "X-Portfolio-Identity";
export const IDENTITY_PROOF_HEADER = "X-Portfolio-Identity-Proof";
export const IDENTITY_PROOF_DOMAIN = "portfolio-identity-v2\n";
export const SERVICE_READ_MAX_AGE_SECONDS = 300;

export type Tier = "ordinary" | "team";

export interface VisitorClaims {
  readonly kind: "visitor";
  readonly ip: string;
  readonly country: string;
  readonly tier: Tier;
  readonly stage: Stage;
  readonly method: string;
  readonly path: string;
}

// A Worker-signed read of public data for the private dev stage. The Worker entry has already
// admitted the visitor, so the claim names the Worker rather than a visitor and is time-bound.
export interface ServiceReadClaims {
  readonly kind: "service-read";
  readonly stage: Stage;
  readonly method: "GET";
  readonly path: string;
  readonly iat: number;
}

export type IdentityClaims = VisitorClaims | ServiceReadClaims;

export interface IdentityHeaders {
  [IDENTITY_HEADER]?: string;
  [IDENTITY_PROOF_HEADER]?: string;
}

export interface IdentityError {
  readonly status: number;
  readonly message: string;
}

export type IdentityEnv = Readonly<Record<string, string | undefined>>;

export async function hmacSha256Hex(secret: string, message: string): Promise<string> {
  const encoder = new TextEncoder();
  const key = await crypto.subtle.importKey(
    "raw",
    encoder.encode(secret),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"]
  );
  const signature = await crypto.subtle.sign("HMAC", key, encoder.encode(message));
  return Array.from(new Uint8Array(signature), (byte) => byte.toString(16).padStart(2, "0")).join("");
}

function toBase64Url(text: string): string {
  const bytes = new TextEncoder().encode(text);
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function fromBase64Url(value: string): string | null {
  if (!/^[A-Za-z0-9_-]+$/.test(value) || value.length % 4 === 1) return null;
  try {
    const binary = atob(value.replace(/-/g, "+").replace(/_/g, "/"));
    const bytes = Uint8Array.from(binary, (char) => char.charCodeAt(0));
    return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  } catch {
    return null;
  }
}

/** Serialises claims with a fixed key order. */
export function encodeIdentity(claims: IdentityClaims): string {
  const ordered =
    claims.kind === "visitor"
      ? {
          v: 2,
          kind: claims.kind,
          ip: claims.ip,
          country: claims.country,
          tier: claims.tier,
          stage: claims.stage,
          method: claims.method,
          path: claims.path,
        }
      : {
          v: 2,
          kind: claims.kind,
          stage: claims.stage,
          method: claims.method,
          path: claims.path,
          iat: claims.iat,
        };
  return toBase64Url(JSON.stringify(ordered));
}

export async function signIdentity(secret: string, claims: IdentityClaims): Promise<IdentityHeaders> {
  const identity = encodeIdentity(claims);
  return {
    [IDENTITY_HEADER]: identity,
    [IDENTITY_PROOF_HEADER]: await hmacSha256Hex(secret, IDENTITY_PROOF_DOMAIN + identity),
  };
}

function isExactObject(value: unknown, keys: readonly string[]): value is Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return false;
  const actual = Object.keys(value);
  return actual.length === keys.length && keys.every((key) => actual.includes(key));
}

const METHOD_PATTERN = /^[A-Z]{3,7}$/;
const PATH_PATTERN = /^\/[\x21-\x7e]{0,255}$/;

/**
 * Strictly decodes an identity header into claims, or null. This is the reference validator
 * the backend mirrors: exact claim set, version 2, canonical IP, uppercase country, a known
 * tier and stage, upper-case method, absolute path.
 */
export function decodeIdentity(identity: string): IdentityClaims | null {
  const json = fromBase64Url(identity);
  if (json === null) return null;

  let claims: unknown;
  try {
    claims = JSON.parse(json);
  } catch {
    return null;
  }

  if (isExactObject(claims, ["v", "kind", "ip", "country", "tier", "stage", "method", "path"])) {
    const { v, kind, ip, country, tier, stage, method, path } = claims;
    const parsedStage = parseStage(stage);
    if (
      v === 2 &&
      kind === "visitor" &&
      typeof ip === "string" &&
      canonicalizeIp(ip) === ip &&
      typeof country === "string" &&
      normalizeCountry(country) === country &&
      (tier === "ordinary" || tier === "team") &&
      parsedStage !== null &&
      typeof method === "string" &&
      METHOD_PATTERN.test(method) &&
      typeof path === "string" &&
      PATH_PATTERN.test(path)
    ) {
      return { kind, ip, country, tier, stage: parsedStage, method, path };
    }
    return null;
  }

  if (isExactObject(claims, ["v", "kind", "stage", "method", "path", "iat"])) {
    const { v, kind, stage, method, path, iat } = claims;
    const parsedStage = parseStage(stage);
    if (
      v === 2 &&
      kind === "service-read" &&
      parsedStage !== null &&
      method === "GET" &&
      typeof path === "string" &&
      PATH_PATTERN.test(path) &&
      typeof iat === "number" &&
      Number.isSafeInteger(iat) &&
      iat > 0
    ) {
      return { kind, stage: parsedStage, method, path, iat };
    }
  }

  return null;
}

export interface IdentityTarget {
  readonly method: string;
  readonly path: string;
}

/**
 * Builds the signed visitor identity for one backend call, or the error to return instead.
 * Fails closed: the stage must be explicit and valid, deployed stages need the shared secret,
 * and the private dev stage additionally needs a valid non-empty allowlist and a visitor on it.
 * The visitor comes from platform metadata only (see readPlatformVisitor).
 */
export async function buildProxyIdentityHeaders(
  request: Request,
  target: IdentityTarget,
  env: IdentityEnv = process.env
): Promise<{ headers: IdentityHeaders; error?: IdentityError }> {
  const stage = parseStage(env.PORTFOLIO_STAGE);
  if (stage === null) {
    return { headers: {}, error: { status: 503, message: "Deployment stage is not configured." } };
  }

  const allowlist = parseTeamAllowlist(env.TEAM_ALLOWLIST);
  if (allowlist.status === "invalid") {
    return { headers: {}, error: { status: 503, message: "Security configuration is invalid." } };
  }
  if (stage === "dev" && (allowlist.status !== "ok" || allowlist.ips.length === 0)) {
    return { headers: {}, error: { status: 503, message: "Private development access is not configured." } };
  }

  const secret = env.ASSISTANT_PROXY_IDENTITY_SECRET;
  if (!secret && stage !== "local") {
    return { headers: {}, error: { status: 503, message: "Assistant identity boundary is not configured." } };
  }

  const visitor = readPlatformVisitor(request);
  if (!visitor) {
    // Local development has no Cloudflare edge; the backend's own development fallback applies.
    if (stage === "local") return { headers: {} };
    return { headers: {}, error: { status: 403, message: "Visitor identity could not be established." } };
  }

  const ips = allowlist.status === "ok" ? allowlist.ips : [];
  const isTeam = isTeamMember(visitor.ip, ips);
  if (stage === "dev" && !isTeam) {
    return { headers: {}, error: { status: 403, message: "Private development environment access denied." } };
  }

  if (!secret) return { headers: {} };

  return {
    headers: await signIdentity(secret, {
      kind: "visitor",
      ip: visitor.ip,
      country: visitor.country,
      tier: isTeam ? "team" : "ordinary",
      stage,
      method: target.method,
      path: target.path,
    }),
  };
}

/**
 * Headers for a server-side read of public data. Only the private dev stage requires them;
 * elsewhere the backend data routes are public and nothing is sent.
 */
export async function buildServiceReadHeaders(
  path: string,
  env: IdentityEnv = process.env,
  nowMs: number = Date.now()
): Promise<{ headers: IdentityHeaders; error?: IdentityError }> {
  const stage = parseStage(env.PORTFOLIO_STAGE);
  if (stage === null) {
    return { headers: {}, error: { status: 503, message: "Deployment stage is not configured." } };
  }
  if (stage !== "dev") return { headers: {} };

  const secret = env.ASSISTANT_PROXY_IDENTITY_SECRET;
  if (!secret) {
    return { headers: {}, error: { status: 503, message: "Assistant identity boundary is not configured." } };
  }

  return {
    headers: await signIdentity(secret, {
      kind: "service-read",
      stage,
      method: "GET",
      path,
      iat: Math.floor(nowMs / 1000),
    }),
  };
}
