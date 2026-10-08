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
    return new Response("ok", { status: 200 });
  }));
  return calls;
}

describe("assistant stream proxy", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.unstubAllGlobals();
  });

  it("signs the edge visitor identity and never forwards caller headers verbatim", async () => {
    vi.stubEnv("ASSISTANT_PROXY_IDENTITY_SECRET", "proxy-test-secret");
    const calls = captureFetch();

    const request = new Request("http://localhost/api/assistant/chat/stream", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "CF-Connecting-IP": "203.0.113.7",
        "X-Forwarded-For": "198.51.100.7",
      },
      body: JSON.stringify({ message: "hello" }),
    });

    const response = await POST(request);
    expect(response.status).toBe(200);

    const headers = calls[0].init.headers as Record<string, string>;
    expect(headers["X-Client-Key"]).toBe("v1:203.0.113.7:XX:ordinary:prod");
    expect(headers["X-Client-Key-Proof"]).toBe(
      await hmacSha256Hex("proxy-test-secret", "v1:203.0.113.7:XX:ordinary:prod")
    );

    // Caller-controlled identity headers must not be forwarded as-is.
    expect(headers["CF-Connecting-IP"]).toBeUndefined();
    expect(headers["X-Forwarded-For"]).toBeUndefined();
  });

  it("returns an explicit 503 in production when the proxy secret is missing", async () => {
    vi.stubEnv("NODE_ENV", "production");
    vi.stubEnv("ASSISTANT_PROXY_IDENTITY_SECRET", "");
    const calls = captureFetch();

    const request = new Request("http://localhost/api/assistant/chat/stream", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "CF-Connecting-IP": "203.0.113.7",
      },
      body: JSON.stringify({ message: "hello" }),
    });

    const response = await POST(request);

    expect(response.status).toBe(503);
    const payload = (await response.json()) as { error?: string };
    expect(payload.error).toContain("not configured");
    expect(calls).toHaveLength(0);
  });

  it("serves production traffic when the proxy secret is configured", async () => {
    vi.stubEnv("NODE_ENV", "production");
    vi.stubEnv("ASSISTANT_PROXY_IDENTITY_SECRET", "proxy-test-secret");
    const calls = captureFetch();

    const request = new Request("http://localhost/api/assistant/chat/stream", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "CF-Connecting-IP": "203.0.113.7",
      },
      body: JSON.stringify({ message: "hello" }),
    });

    const response = await POST(request);

    expect(response.status).toBe(200);
    const headers = calls[0].init.headers as Record<string, string>;
    expect(headers["X-Client-Key"]).toBe("v1:203.0.113.7:XX:ordinary:prod");
  });

  it("sends no identity headers when the proxy secret is not configured", async () => {
    vi.stubEnv("PORTFOLIO_STAGE", "dev");
    vi.stubEnv("TEAM_ALLOWLIST", "203.0.113.7");
    vi.stubEnv("ASSISTANT_PROXY_IDENTITY_SECRET", "");
    const calls = captureFetch();

    const request = new Request("http://localhost/api/assistant/chat/stream", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "CF-Connecting-IP": "203.0.113.7",
      },
      body: JSON.stringify({ message: "hello" }),
    });

    await POST(request);

    const headers = calls[0].init.headers as Record<string, string>;
    expect(headers["X-Client-Key"]).toBeUndefined();
    expect(headers["X-Client-Key-Proof"]).toBeUndefined();
    expect(headers["CF-Connecting-IP"]).toBeUndefined();
  });

  it("sends no identity headers when the visitor id is unavailable", async () => {
    vi.stubEnv("ASSISTANT_PROXY_IDENTITY_SECRET", "proxy-test-secret");
    const calls = captureFetch();

    const request = new Request("http://localhost/api/assistant/chat/stream", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ message: "hello" }),
    });

    await POST(request);

    const headers = calls[0].init.headers as Record<string, string>;
    expect(headers["X-Client-Key"]).toBeUndefined();
    expect(headers["X-Client-Key-Proof"]).toBeUndefined();
  });
});
