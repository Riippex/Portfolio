import { hmacSha256Hex, IDENTITY_HEADER, IDENTITY_PROOF_HEADER } from "./identity";
import { parseStage } from "./stage";
import { isTeamMember, parseTeamAllowlist } from "./team-allowlist";
import { readPlatformVisitor, type PlatformVisitor } from "./visitor";

// Admission decisions made at the Worker entry (frontend/worker/entry.ts), before the
// request reaches static assets, caches, HTML, RSC, image optimisation or any API route.
//
//  - dev is private: only a visitor whose platform-asserted IP is on the dev allowlist is
//    admitted, for every path except the exact content-free health endpoint.
//  - prod is public, with finite edge limits on the model-backed routes.
//  - an unset or unknown stage, a missing/invalid required allowlist, or a missing
//    rate-limit binding fails closed.
//
// Edge limits use Cloudflare Workers Rate Limiting bindings. Those counters are kept per
// Cloudflare location and are eventually consistent, so they are an approximate first line,
// not an exact global quota. The backend enforces its own independent limits.

export const HEALTH_PATH = "/health";

// The shared chat/job quota. Contact keeps its own separate backend quota.
const EDGE_LIMITED_PATHS: ReadonlySet<string> = new Set(["/api/assistant/chat/stream", "/api/jobs/analyze"]);

export const EDGE_LIMITS = { ordinaryIpPerMinute: 5, teamIpPerMinute: 15, countryPerMinute: 100 } as const;
export const EDGE_LIMIT_PERIOD_SECONDS = 60;

export interface RateLimitBinding {
  limit(options: { key: string }): Promise<{ success: boolean }>;
}

export interface AdmissionEnv {
  readonly PORTFOLIO_STAGE?: unknown;
  // Server-only secret that keys the pseudonym sent to the rate-limit bindings.
  readonly ASSISTANT_PROXY_IDENTITY_SECRET?: unknown;
  readonly TEAM_ALLOWLIST?: unknown;
  readonly RATE_LIMIT_IP_ORDINARY?: RateLimitBinding;
  readonly RATE_LIMIT_IP_TEAM?: RateLimitBinding;
  readonly RATE_LIMIT_COUNTRY?: RateLimitBinding;
}

export type AdmissionDecision =
  | { readonly action: "respond"; readonly response: Response }
  | { readonly action: "forward"; readonly request: Request };

function denial(status: number, error: string, extraHeaders: Record<string, string> = {}): AdmissionDecision {
  return {
    action: "respond",
    response: new Response(JSON.stringify({ error }), {
      status,
      headers: {
        "Content-Type": "application/json",
        "Cache-Control": "private, no-store",
        "X-Content-Type-Options": "nosniff",
        ...extraHeaders,
      },
    }),
  };
}

// Normalises a path for policy matching only, so encoded, repeated-slash, trailing-slash or
// differently cased spellings of a limited route cannot slip past the exact-path set. The
// request itself is forwarded unchanged.
export function policyPath(pathname: string): string {
  let path = pathname;
  for (let round = 0; round < 2; round++) {
    try {
      path = decodeURIComponent(path);
    } catch {
      break;
    }
  }
  path = path.replace(/\\/g, "/").replace(/\/{2,}/g, "/").toLowerCase();
  return path.length > 1 ? path.replace(/\/+$/, "") : path;
}

// Domain separator for the edge counter pseudonym; distinct from the identity proof domain so
// one secret never yields a value usable as the other.
export const EDGE_KEY_DOMAIN = "portfolio-edge-limit-v1\n";

/**
 * The key the rate-limit bindings count under: a stable HMAC pseudonym of the canonical
 * address, bound to the stage and to nothing else (not the endpoint), so chat and job analysis
 * keep one shared quota and equivalent IPv6 spellings share one counter. The raw address
 * never leaves the request.
 */
export async function edgeCounterKey(secret: string, stage: string, canonicalIp: string): Promise<string> {
  return `ip:${await hmacSha256Hex(secret, `${EDGE_KEY_DOMAIN}${stage}\nip\n${canonicalIp}`)}`;
}

function withPlatformVisitor(request: Request, visitor: PlatformVisitor | null): Request {
  const headers = new Headers(request.headers);
  // Whatever the caller sent is discarded; only values derived from platform metadata remain.
  headers.delete(IDENTITY_HEADER);
  headers.delete(IDENTITY_PROOF_HEADER);
  headers.delete("CF-IPCountry");
  headers.delete("CF-Connecting-IP");
  if (visitor) {
    headers.set("CF-Connecting-IP", visitor.ip);
    headers.set("CF-IPCountry", visitor.country);
  }
  return new Request(request, { headers });
}

export async function admitRequest(request: Request, env: AdmissionEnv): Promise<AdmissionDecision> {
  const url = new URL(request.url);

  if (url.pathname === HEALTH_PATH && (request.method === "GET" || request.method === "HEAD")) {
    return {
      action: "respond",
      response: new Response(request.method === "HEAD" ? null : JSON.stringify({ status: "ok" }), {
        status: 200,
        headers: { "Content-Type": "application/json", "Cache-Control": "no-store" },
      }),
    };
  }

  // Deployed Workers run as dev or prod only; "local" is for workstation tooling.
  const stage = parseStage(env.PORTFOLIO_STAGE);
  if (stage !== "dev" && stage !== "prod") {
    return denial(503, "Deployment stage is not configured.");
  }

  const allowlist = parseTeamAllowlist(env.TEAM_ALLOWLIST);
  const visitor = readPlatformVisitor(request);

  if (stage === "dev") {
    if (allowlist.status !== "ok" || allowlist.ips.length === 0) {
      return denial(503, "Private development access is not configured.");
    }
    if (!visitor || !isTeamMember(visitor.ip, allowlist.ips)) {
      return denial(403, "Access denied.");
    }
  }

  if (EDGE_LIMITED_PATHS.has(policyPath(url.pathname))) {
    if (allowlist.status === "invalid") {
      return denial(503, "Security configuration is invalid.");
    }
    if (!visitor) {
      return denial(403, "Visitor identity could not be established.");
    }

    const isTeam = allowlist.status === "ok" && isTeamMember(visitor.ip, allowlist.ips);
    const ipLimiter = isTeam ? env.RATE_LIMIT_IP_TEAM : env.RATE_LIMIT_IP_ORDINARY;
    const countryLimiter = env.RATE_LIMIT_COUNTRY;
    if (!ipLimiter || !countryLimiter) {
      return denial(503, "Edge rate limiting is not configured.");
    }
    const keySecret = env.ASSISTANT_PROXY_IDENTITY_SECRET;
    if (typeof keySecret !== "string" || keySecret.trim() === "") {
      // Without the secret the only available key would be the raw address: refuse instead.
      return denial(503, "Edge rate limiting is not configured.");
    }

    const retryAfter = { "Retry-After": String(EDGE_LIMIT_PERIOD_SECONDS) };
    try {
      // The per-IP check runs first so a request refused for its own address does not also
      // consume country capacity. Team traffic still counts toward its country's limit.
      const ipKey = await edgeCounterKey(keySecret, stage, visitor.ip);
      if (!(await ipLimiter.limit({ key: ipKey })).success) {
        return denial(429, "Rate limit exceeded. Please wait before trying again.", retryAfter);
      }
      if (!(await countryLimiter.limit({ key: `country:${visitor.country}` })).success) {
        return denial(429, "Rate limit exceeded. Please wait before trying again.", retryAfter);
      }
    } catch {
      return denial(503, "Edge rate limiting is unavailable.");
    }
  }

  return { action: "forward", request: withPlatformVisitor(request, visitor) };
}
