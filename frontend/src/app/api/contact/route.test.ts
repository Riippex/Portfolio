import { afterEach, describe, expect, it, vi } from "vitest";
import {
  captureFetch,
  expectNoCallerIdentity,
  expectSignedIdentity,
  PROXY_SECRET,
} from "@/modules/security/proxy-test-support";
import { POST } from "./route";

const CONTACT_BODY = {
  name: "Jane Doe",
  email: "jane@example.com",
  message: "Hello Rafael, inquiry about agents.",
  consent: true,
};

function contactRequest(headers: Record<string, string> = {}, body: string = JSON.stringify(CONTACT_BODY)) {
  return new Request("http://localhost/api/contact", {
    method: "POST",
    headers: { "Content-Type": "application/json", ...headers },
    body,
  });
}

function configure(env: Record<string, string>) {
  for (const [name, value] of Object.entries({ PORTFOLIO_STAGE: "prod", ASSISTANT_PROXY_IDENTITY_SECRET: PROXY_SECRET, ...env })) {
    vi.stubEnv(name, value);
  }
}

describe("contact proxy route", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.unstubAllGlobals();
  });

  it("signs the visitor identity for the contact endpoint and forwards the request", async () => {
    configure({});
    const calls = captureFetch({ status: "delivered", outcome: "delivered" });

    const response = await POST(contactRequest({ "CF-Connecting-IP": "198.51.100.22", "CF-IPCountry": "ES" }));
    expect(response.status).toBe(200);

    expect(calls).toHaveLength(1);
    expect(calls[0].url).toMatch(/\/v1\/contact$/);
    expect(await expectSignedIdentity(calls[0])).toEqual({
      kind: "visitor",
      ip: "198.51.100.22",
      country: "ES",
      tier: "ordinary",
      stage: "prod",
      method: "POST",
      path: "/v1/contact",
    });
    expectNoCallerIdentity(calls[0]);
  });

  it("gives team visitors no way around the contact controls: it signs the tier and nothing else", async () => {
    configure({ TEAM_ALLOWLIST: '["198.51.100.22"]' });
    const calls = captureFetch();

    await POST(contactRequest({ "CF-Connecting-IP": "198.51.100.22" }));

    expect(await expectSignedIdentity(calls[0])).toMatchObject({ tier: "team", path: "/v1/contact" });
  });

  it("rejects request bodies larger than 64 KiB with 413", async () => {
    configure({});
    const calls = captureFetch();
    const oversizedBody = JSON.stringify({ ...CONTACT_BODY, message: "x".repeat(65 * 1024) });

    const response = await POST(
      contactRequest({ "CF-Connecting-IP": "198.51.100.22", "Content-Length": String(oversizedBody.length) }, oversizedBody)
    );

    expect(response.status).toBe(413);
    expect(((await response.json()) as { error: string }).error).toContain("64 KiB");
    expect(calls).toHaveLength(0);
  });

  it("returns 503 when the stage is not configured", async () => {
    configure({ PORTFOLIO_STAGE: "" });
    const calls = captureFetch();

    const response = await POST(contactRequest({ "CF-Connecting-IP": "198.51.100.22" }));

    expect(response.status).toBe(503);
    expect(calls).toHaveLength(0);
  });

  it("returns 503 when the proxy secret is missing", async () => {
    configure({ ASSISTANT_PROXY_IDENTITY_SECRET: "" });
    const calls = captureFetch();

    const response = await POST(contactRequest({ "CF-Connecting-IP": "198.51.100.22" }));

    expect(response.status).toBe(503);
    expect(calls).toHaveLength(0);
  });

  it("refuses a request whose platform IP is missing, ignoring forwarding headers", async () => {
    configure({});
    const calls = captureFetch();

    const response = await POST(contactRequest({ "X-Forwarded-For": "198.51.100.22" }));

    expect(response.status).toBe(403);
    expect(calls).toHaveLength(0);
  });

  it("denies non-allowlisted visitors in the private dev stage", async () => {
    configure({ PORTFOLIO_STAGE: "dev", TEAM_ALLOWLIST: '["203.0.113.4"]' });
    const calls = captureFetch();

    const response = await POST(contactRequest({ "CF-Connecting-IP": "198.51.100.22" }));

    expect(response.status).toBe(403);
    expect(calls).toHaveLength(0);
  });

  it("forwards backend error status and Retry-After header", async () => {
    configure({});
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => {
        return new Response(JSON.stringify({ error: "Rate limit exceeded" }), {
          status: 429,
          headers: { "Retry-After": "600", "Content-Type": "application/json" },
        });
      })
    );

    const response = await POST(contactRequest({ "CF-Connecting-IP": "198.51.100.22" }));
    expect(response.status).toBe(429);
    expect(response.headers.get("Retry-After")).toBe("600");
  });

  it("returns 502 on upstream network error", async () => {
    configure({});
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => {
        throw new Error("Connection refused");
      })
    );

    const response = await POST(contactRequest({ "CF-Connecting-IP": "198.51.100.22" }));
    expect(response.status).toBe(502);
  });
});
