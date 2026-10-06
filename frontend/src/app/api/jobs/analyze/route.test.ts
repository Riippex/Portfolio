import { afterEach, describe, expect, it, vi } from "vitest";
import { POST } from "./route";

async function hmacSha256Hex(secret: string, message: string): Promise<string> {
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

function captureFetch() {
  const calls: { url: string; init: RequestInit }[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
    calls.push({ url: String(url), init });
    return new Response(JSON.stringify({ ok: true }), { status: 200, headers: { "Content-Type": "application/json" } });
  }));
  return calls;
}

describe("job matching proxy route", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.unstubAllGlobals();
  });

  it("signs the edge visitor identity and forwards request to backend", async () => {
    vi.stubEnv("ASSISTANT_PROXY_IDENTITY_SECRET", "proxy-test-secret");
    const calls = captureFetch();

    const request = new Request("http://localhost/api/jobs/analyze", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "CF-Connecting-IP": "203.0.113.12",
      },
      body: JSON.stringify({ vacancyText: "Looking for an Autonomous Agents architect." }),
    });

    const response = await POST(request);
    expect(response.status).toBe(200);

    const headers = calls[0].init.headers as Record<string, string>;
    expect(headers["X-Client-Key"]).toBe("203.0.113.12");
    expect(headers["X-Client-Key-Proof"]).toBe(
      await hmacSha256Hex("proxy-test-secret", "203.0.113.12")
    );
    expect(headers["CF-Connecting-IP"]).toBeUndefined();
  });

  it("returns 503 in production when the proxy secret is missing", async () => {
    vi.stubEnv("NODE_ENV", "production");
    vi.stubEnv("ASSISTANT_PROXY_IDENTITY_SECRET", "");
    const calls = captureFetch();

    const request = new Request("http://localhost/api/jobs/analyze", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "CF-Connecting-IP": "203.0.113.12",
      },
      body: JSON.stringify({ vacancyText: "Looking for an Autonomous Agents architect." }),
    });

    const response = await POST(request);
    expect(response.status).toBe(503);
    const payload = (await response.json()) as { error?: string };
    expect(payload.error).toContain("not configured");
    expect(calls).toHaveLength(0);
  });

  it("forwards backend error status and Retry-After header", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => {
      return new Response(JSON.stringify({ error: "Rate limit exceeded" }), {
        status: 429,
        headers: { "Retry-After": "60", "Content-Type": "application/json" },
      });
    }));

    const request = new Request("http://localhost/api/jobs/analyze", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ vacancyText: "Looking for an Autonomous Agents architect." }),
    });

    const response = await POST(request);
    expect(response.status).toBe(429);
    expect(response.headers.get("Retry-After")).toBe("60");
  });
});
