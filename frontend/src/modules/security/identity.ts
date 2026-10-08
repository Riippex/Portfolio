import { canonicalizeIp, parseTeamAllowlist, isTeamMember } from "./team-allowlist";

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

export interface ProxyIdentityHeaders {
  "X-Client-Key"?: string;
  "X-Client-Country"?: string;
  "X-Client-Tier"?: string;
  "X-Client-Stage"?: string;
  "X-Client-Key-Proof"?: string;
}

export async function buildProxyIdentityHeaders(request: Request): Promise<{
  headers: ProxyIdentityHeaders;
  error?: { status: number; message: string };
}> {
  const identitySecret = process.env.ASSISTANT_PROXY_IDENTITY_SECRET;
  const rawStage = (
    process.env.PORTFOLIO_STAGE ||
    process.env.STAGE ||
    process.env.NEXT_PUBLIC_APP_STAGE ||
    (process.env.NODE_ENV === "development" ? "dev" : "prod")
  ).toLowerCase();
  const stage = rawStage === "development" ? "dev" : rawStage === "production" ? "prod" : rawStage;

  const allowlistParsed = parseTeamAllowlist(process.env.TEAM_ALLOWLIST);

  if (stage === "dev") {
    if (!allowlistParsed.valid || allowlistParsed.ips.length === 0) {
      return {
        headers: {},
        error: { status: 503, message: "Dev private access control is unconfigured or invalid." },
      };
    }
  } else if (stage === "prod") {
    if (!allowlistParsed.valid) {
      return {
        headers: {},
        error: { status: 503, message: "Security configuration is invalid." },
      };
    }
  }

  if (!identitySecret && stage === "prod") {
    return {
      headers: {},
      error: { status: 503, message: "Assistant identity boundary is not configured." },
    };
  }

  const rawIp =
    request.headers.get("CF-Connecting-IP")?.trim() ||
    request.headers.get("x-forwarded-for")?.split(",")[0]?.trim();
  const visitorIp = canonicalizeIp(rawIp || "");
  const country = (request.headers.get("CF-IPCountry")?.trim() || "XX").toUpperCase();

  if (stage === "dev") {
    if (!visitorIp || !isTeamMember(visitorIp, allowlistParsed.ips)) {
      return {
        headers: {},
        error: { status: 403, message: "Private development environment access denied." },
      };
    }
  }

  const isTeam = visitorIp ? isTeamMember(visitorIp, allowlistParsed.ips) : false;
  const tier = isTeam ? "team" : "ordinary";

  const headers: ProxyIdentityHeaders = {};
  if (identitySecret && visitorIp) {
    const payload = `v1:${visitorIp}:${country}:${tier}:${stage}`;
    headers["X-Client-Key"] = payload;
    headers["X-Client-Country"] = country;
    headers["X-Client-Tier"] = tier;
    headers["X-Client-Stage"] = stage;
    headers["X-Client-Key-Proof"] = await hmacSha256Hex(identitySecret, payload);
  }

  return { headers };
}
