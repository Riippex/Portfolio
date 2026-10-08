import { afterEach, describe, expect, it, vi } from "vitest";
import {
  captureFetch,
  expectNoCallerIdentity,
  expectSignedIdentity,
  PROXY_SECRET,
} from "@/modules/security/proxy-test-support";
import { POST } from "./route";

function jobRequest(headers: Record<string, string> = {}) {
  return new Request("http://localhost/api/jobs/analyze", {
    method: "POST",
    headers: { "Content-Type": "application/json", ...headers },
    body: JSON.stringify({ vacancyText: "Looking for an Autonomous Agents architect." }),
  });
}

function configure(env: Record<string, string>) {
  for (const [name, value] of Object.entries({ PORTFOLIO_STAGE: "prod", ASSISTANT_PROXY_IDENTITY_SECRET: PROXY_SECRET, ...env })) {
    vi.stubEnv(name, value);
  }
}

describe("job matching proxy route", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.unstubAllGlobals();
  });

  it("signs the platform visitor identity for the job endpoint", async () => {
    configure({});
    const calls = captureFetch();

    const response = await jobRequestResponse({ "CF-Connecting-IP": "203.0.113.12", "CF-IPCountry": "MX" });
    expect(response.status).toBe(200);

    expect(calls[0].url).toMatch(/\/v1\/jobs\/analyze$/);
    expect(await expectSignedIdentity(calls[0])).toEqual({
      kind: "visitor",
      ip: "203.0.113.12",
      country: "MX",
      tier: "ordinary",
      stage: "prod",
      method: "POST",
      path: "/v1/jobs/analyze",
    });
    expectNoCallerIdentity(calls[0]);
  });

  it("returns 503 when the stage is not configured", async () => {
    configure({ PORTFOLIO_STAGE: "" });
    const calls = captureFetch();

    const response = await jobRequestResponse({ "CF-Connecting-IP": "203.0.113.12" });

    expect(response.status).toBe(503);
    expect(calls).toHaveLength(0);
  });

  it("returns 503 when the proxy secret is missing", async () => {
    configure({ ASSISTANT_PROXY_IDENTITY_SECRET: "" });
    const calls = captureFetch();

    const response = await jobRequestResponse({ "CF-Connecting-IP": "203.0.113.12" });

    expect(response.status).toBe(503);
    expect(((await response.json()) as { error?: string }).error).toContain("not configured");
    expect(calls).toHaveLength(0);
  });

  it("refuses a request whose platform IP is missing, ignoring forwarding headers", async () => {
    configure({});
    const calls = captureFetch();

    const response = await jobRequestResponse({ "X-Forwarded-For": "198.51.100.7" });

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
          headers: { "Retry-After": "60", "Content-Type": "application/json" },
        });
      })
    );

    const response = await jobRequestResponse({ "CF-Connecting-IP": "203.0.113.12" });
    expect(response.status).toBe(429);
    expect(response.headers.get("Retry-After")).toBe("60");
  });
});

function jobRequestResponse(headers: Record<string, string>) {
  return POST(jobRequest(headers));
}
