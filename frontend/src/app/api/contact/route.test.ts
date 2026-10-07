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

function captureFetch(responseStatus = 200, responseBody = { status: "delivered", outcome: "delivered" }) {
  const calls: { url: string; init: RequestInit }[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
    calls.push({ url: String(url), init });
    return new Response(JSON.stringify(responseBody), {
      status: responseStatus,
      headers: { "Content-Type": "application/json" },
    });
  }));
  return calls;
}

describe("contact proxy route", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.unstubAllGlobals();
  });

  it("signs the visitor identity and forwards request to backend", async () => {
    vi.stubEnv("ASSISTANT_PROXY_IDENTITY_SECRET", "contact-proxy-secret");
    const calls = captureFetch();

    const request = new Request("http://localhost/api/contact", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "CF-Connecting-IP": "198.51.100.22",
      },
      body: JSON.stringify({
        name: "Jane Doe",
        email: "jane@example.com",
        message: "Hello Rafael, inquiry about agents.",
        consent: true,
      }),
    });

    const response = await POST(request);
    expect(response.status).toBe(200);

    expect(calls).toHaveLength(1);
    expect(calls[0].url).toContain("/v1/contact");
    const headers = calls[0].init.headers as Record<string, string>;
    expect(headers["X-Client-Key"]).toBe("198.51.100.22");
    expect(headers["X-Client-Key-Proof"]).toBe(
      await hmacSha256Hex("contact-proxy-secret", "198.51.100.22")
    );
  });

  it("rejects request bodies larger than 64 KiB with 413", async () => {
    const calls = captureFetch();
    const oversizedBody = JSON.stringify({
      name: "Jane Doe",
      email: "jane@example.com",
      message: "x".repeat(65 * 1024),
      consent: true,
    });

    const request = new Request("http://localhost/api/contact", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "Content-Length": String(oversizedBody.length),
      },
      body: oversizedBody,
    });

    const response = await POST(request);
    expect(response.status).toBe(413);
    const data = await response.json();
    expect(data.error).toContain("64 KiB");
    expect(calls).toHaveLength(0);
  });

  it("returns 503 in production when the proxy secret is missing", async () => {
    vi.stubEnv("NODE_ENV", "production");
    vi.stubEnv("ASSISTANT_PROXY_IDENTITY_SECRET", "");
    const calls = captureFetch();

    const request = new Request("http://localhost/api/contact", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "CF-Connecting-IP": "198.51.100.22",
      },
      body: JSON.stringify({
        name: "Jane Doe",
        email: "jane@example.com",
        message: "Hello Rafael.",
        consent: true,
      }),
    });

    const response = await POST(request);
    expect(response.status).toBe(503);
    expect(calls).toHaveLength(0);
  });

  it("forwards backend error status and Retry-After header", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => {
      return new Response(JSON.stringify({ error: "Rate limit exceeded" }), {
        status: 429,
        headers: { "Retry-After": "600", "Content-Type": "application/json" },
      });
    }));

    const request = new Request("http://localhost/api/contact", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        name: "Jane Doe",
        email: "jane@example.com",
        message: "Hello Rafael.",
        consent: true,
      }),
    });

    const response = await POST(request);
    expect(response.status).toBe(429);
    expect(response.headers.get("Retry-After")).toBe("600");
  });

  it("returns 502 on upstream network error", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => {
      throw new Error("Connection refused");
    }));

    const request = new Request("http://localhost/api/contact", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        name: "Jane Doe",
        email: "jane@example.com",
        message: "Hello Rafael.",
        consent: true,
      }),
    });

    const response = await POST(request);
    expect(response.status).toBe(502);
  });
});
