import { afterEach, describe, expect, it, vi } from "vitest";
import { IDENTITY_HEADER, IDENTITY_PROOF_HEADER } from "@/modules/security/identity";
import {
  captureFetch,
  expectNoCallerIdentity,
  expectSignedIdentity,
  PROXY_SECRET,
  sentHeaders,
} from "@/modules/security/proxy-test-support";
import { POST } from "./route";

function streamRequest(headers: Record<string, string> = {}) {
  return new Request("http://localhost/api/assistant/chat/stream", {
    method: "POST",
    headers: { "Content-Type": "application/json", ...headers },
    body: JSON.stringify({ message: "hello" }),
  });
}

function configure(env: Record<string, string>) {
  for (const [name, value] of Object.entries({ PORTFOLIO_STAGE: "prod", ASSISTANT_PROXY_IDENTITY_SECRET: PROXY_SECRET, ...env })) {
    vi.stubEnv(name, value);
  }
}

describe("assistant stream proxy", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.unstubAllGlobals();
  });

  it("signs the platform visitor identity and never forwards caller headers", async () => {
    configure({});
    const calls = captureFetch();

    const response = await POST(
      streamRequest({ "CF-Connecting-IP": "203.0.113.7", "CF-IPCountry": "CO", "X-Forwarded-For": "198.51.100.7" })
    );
    expect(response.status).toBe(200);

    expect(calls[0].url).toMatch(/\/v1\/assistant\/chat\/stream$/);
    expect(await expectSignedIdentity(calls[0])).toEqual({
      kind: "visitor",
      ip: "203.0.113.7",
      country: "CO",
      tier: "ordinary",
      stage: "prod",
      method: "POST",
      path: "/v1/assistant/chat/stream",
    });
    expectNoCallerIdentity(calls[0]);
  });

  it("signs IPv6 visitors in canonical form", async () => {
    configure({});
    const calls = captureFetch();

    await POST(streamRequest({ "CF-Connecting-IP": "2001:DB8:0:0:0:0:0:1" }));

    expect(await expectSignedIdentity(calls[0])).toMatchObject({ ip: "2001:db8::1", country: "XX" });
  });

  it("marks allowlisted visitors as team while still sending the request to the backend limiter and Turnstile", async () => {
    configure({ TEAM_ALLOWLIST: '["203.0.113.7"]' });
    const calls = captureFetch();

    await POST(streamRequest({ "CF-Connecting-IP": "203.0.113.7" }));

    expect(await expectSignedIdentity(calls[0])).toMatchObject({ tier: "team" });
    expect(calls).toHaveLength(1);
  });

  it("fails closed with 503 when the stage is missing or invalid, even if NODE_ENV looks like dev", async () => {
    for (const stage of ["", "development", "production", "staging"]) {
      configure({ PORTFOLIO_STAGE: stage, NODE_ENV: "development" });
      const calls = captureFetch();

      const response = await POST(streamRequest({ "CF-Connecting-IP": "203.0.113.7" }));

      expect(response.status).toBe(503);
      expect(calls).toHaveLength(0);
      vi.unstubAllEnvs();
    }
  });

  it("returns an explicit 503 when the proxy secret is missing outside local development", async () => {
    configure({ ASSISTANT_PROXY_IDENTITY_SECRET: "" });
    const calls = captureFetch();

    const response = await POST(streamRequest({ "CF-Connecting-IP": "203.0.113.7" }));

    expect(response.status).toBe(503);
    expect(((await response.json()) as { error?: string }).error).toContain("not configured");
    expect(calls).toHaveLength(0);
  });

  it("refuses a request without a platform IP and does not use forwarding headers", async () => {
    configure({});
    const calls = captureFetch();

    const response = await POST(streamRequest({ "X-Forwarded-For": "198.51.100.7", "X-Real-IP": "198.51.100.7" }));

    expect(response.status).toBe(403);
    expect(calls).toHaveLength(0);
  });

  it("denies non-allowlisted visitors in dev and fails closed without an allowlist", async () => {
    configure({ PORTFOLIO_STAGE: "dev", TEAM_ALLOWLIST: '["203.0.113.7"]' });
    let calls = captureFetch();
    expect((await POST(streamRequest({ "CF-Connecting-IP": "198.51.100.9" }))).status).toBe(403);
    expect(calls).toHaveLength(0);

    vi.stubEnv("TEAM_ALLOWLIST", "");
    calls = captureFetch();
    expect((await POST(streamRequest({ "CF-Connecting-IP": "203.0.113.7" }))).status).toBe(503);
    expect(calls).toHaveLength(0);
  });

  it("signs the dev stage into the identity for allowlisted visitors", async () => {
    configure({ PORTFOLIO_STAGE: "dev", TEAM_ALLOWLIST: '["203.0.113.7"]' });
    const calls = captureFetch();

    await POST(streamRequest({ "CF-Connecting-IP": "203.0.113.7" }));

    expect(await expectSignedIdentity(calls[0])).toMatchObject({ stage: "dev", tier: "team" });
  });

  it("sends no identity in the explicit local stage, leaving the backend development fallback", async () => {
    configure({ PORTFOLIO_STAGE: "local", ASSISTANT_PROXY_IDENTITY_SECRET: "" });
    const calls = captureFetch();

    await POST(streamRequest({ "CF-Connecting-IP": "203.0.113.7" }));

    expect(sentHeaders(calls[0])[IDENTITY_HEADER]).toBeUndefined();
    expect(sentHeaders(calls[0])[IDENTITY_PROOF_HEADER]).toBeUndefined();
  });
});
