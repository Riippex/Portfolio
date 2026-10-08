import { vi } from "vitest";
import { decodeIdentity, hmacSha256Hex, IDENTITY_HEADER, IDENTITY_PROOF_DOMAIN, IDENTITY_PROOF_HEADER } from "./identity";

export const PROXY_SECRET = "proxy-test-secret";

export function captureFetch(body: unknown = { ok: true }, status = 200) {
  const calls: { url: string; init: RequestInit }[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url: String(url), init });
      return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
    })
  );
  return calls;
}

export const sentHeaders = (call: { init: RequestInit }) => call.init.headers as Record<string, string>;

/** Asserts the backend call carried a valid v2 identity and returns its claims. */
export async function expectSignedIdentity(call: { init: RequestInit }, secret = PROXY_SECRET) {
  const headers = sentHeaders(call);
  const claims = decodeIdentity(headers[IDENTITY_HEADER]);
  if (!claims) throw new Error("backend call carried no valid identity");
  const expectedProof = await hmacSha256Hex(secret, IDENTITY_PROOF_DOMAIN + headers[IDENTITY_HEADER]);
  if (headers[IDENTITY_PROOF_HEADER] !== expectedProof) throw new Error("identity proof does not match");
  return claims;
}

/** Caller-controlled and legacy identity headers must never reach the backend as sent. */
export function expectNoCallerIdentity(call: { init: RequestInit }) {
  const headers = sentHeaders(call);
  for (const name of ["CF-Connecting-IP", "CF-IPCountry", "X-Forwarded-For", "X-Client-Key", "X-Client-Key-Proof", "X-Client-Tier"]) {
    if (headers[name] !== undefined) throw new Error(`${name} was forwarded to the backend`);
  }
}
