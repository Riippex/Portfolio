import { afterEach, describe, expect, it, vi } from "vitest";
import { sendContactMessage } from "./api";
import { parseContactRelaySuccess } from "./model";

const request = {
  name: "Jane Doe",
  email: "jane@example.com",
  message: "Hello Rafael, inquiry about your work.",
  consent: true,
};

function stubFetch(status: number, body: unknown, headers: Record<string, string> = {}) {
  const fetchMock = vi.fn(async () =>
    new Response(typeof body === "string" ? body : JSON.stringify(body), {
      status,
      headers: { "Content-Type": "application/json", ...headers },
    })
  );
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

describe("sendContactMessage", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it.each(["delivered", "queued"] as const)("accepts the documented %s payload", async (status) => {
    const fetchMock = stubFetch(200, { status, outcome: status });

    const result = await sendContactMessage(request);

    expect(result).toEqual({ ok: true, data: { status, outcome: status } });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it.each([
    ["a failed status with an outcome", { status: "failed", outcome: "delivery_unconfirmed" }],
    ["an unknown status", { status: "sent", outcome: "sent" }],
    ["an accepted status", { status: "accepted", outcome: "accepted" }],
    ["a missing status", { outcome: "delivered" }],
    ["a missing outcome", { status: "delivered" }],
    ["a mismatched outcome", { status: "delivered", outcome: "queued" }],
    ["a non-string status", { status: true, outcome: true }],
    ["an array", ["delivered"]],
    ["null", null],
    ["a bare string", "delivered"],
    ["an empty object", {}],
  ])("rejects HTTP 200 with %s instead of reporting delivery", async (_label, body) => {
    stubFetch(200, body);

    const result = await sendContactMessage(request);

    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.status).toBe(200);
      expect(result.error).toMatch(/unexpected response/i);
    }
  });

  it("rejects an HTTP 200 body that is not JSON", async () => {
    stubFetch(200, "<html>proxy error</html>");

    const result = await sendContactMessage(request);

    expect(result.ok).toBe(false);
  });

  it.each([400, 413, 429, 500, 502, 503])(
    "never treats HTTP %i as success even when the body claims delivery",
    async (status) => {
      stubFetch(status, { status: "delivered", outcome: "delivered", error: "Provider failed." });

      const result = await sendContactMessage(request);

      expect(result.ok).toBe(false);
      if (!result.ok) {
        expect(result.status).toBe(status);
      }
    }
  );

  it("reports the retry window for a rate-limited response", async () => {
    stubFetch(429, { error: "Rate limit exceeded." }, { "Retry-After": "600" });

    const result = await sendContactMessage(request);

    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.error).toMatch(/600 seconds/);
    }
  });

  it("reports a network failure without retrying", async () => {
    const fetchMock = vi.fn(async () => {
      throw new Error("network down");
    });
    vi.stubGlobal("fetch", fetchMock);

    const result = await sendContactMessage(request);

    expect(result).toEqual({ ok: false, error: "network down", status: 0 });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("makes exactly one request per call and never retries an error response", async () => {
    const fetchMock = stubFetch(502, { error: "Contact message delivery failed.", outcome: "timeout" });

    const result = await sendContactMessage(request);

    expect(result.ok).toBe(false);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});

describe("parseContactRelaySuccess", () => {
  it("accepts only the two documented payloads", () => {
    expect(parseContactRelaySuccess({ status: "delivered", outcome: "delivered" })).toEqual({
      status: "delivered",
      outcome: "delivered",
    });
    expect(parseContactRelaySuccess({ status: "queued", outcome: "queued" })).toEqual({
      status: "queued",
      outcome: "queued",
    });
    expect(parseContactRelaySuccess({ status: "Delivered", outcome: "Delivered" })).toBeNull();
    expect(parseContactRelaySuccess(undefined)).toBeNull();
  });
});
