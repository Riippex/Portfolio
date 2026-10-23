import { afterEach, describe, expect, it, vi } from "vitest";

const handlerFetch = vi.hoisted(() => vi.fn(async () => new Response("app")));

vi.mock("vinext/server/fetch-handler", () => ({ default: { fetch: handlerFetch } }));

import worker from "./entry";

const TEAM_IP = "203.0.113.4";
const ctx = {};

const devEnv = () => ({
  PORTFOLIO_STAGE: "dev",
  TEAM_ALLOWLIST: JSON.stringify([TEAM_IP]),
});

describe("Worker entry", () => {
  afterEach(() => {
    handlerFetch.mockClear();
  });

  it("denies before the application or assets are reached in dev", async () => {
    const assetFetch = vi.fn();
    for (const path of ["/", "/_next/static/chunks/app.js", "/_next/image?url=%2Fx&w=64&q=75", "/favicon.ico", "/api/jobs/analyze"]) {
      const response = await worker.fetch(
        new Request(`https://dev.example${path}`, { headers: { "CF-Connecting-IP": "198.51.100.8" } }),
        { ...devEnv(), ASSETS: { fetch: assetFetch } },
        ctx
      );
      expect(response.status).toBe(403);
    }
    expect(handlerFetch).not.toHaveBeenCalled();
    expect(assetFetch).not.toHaveBeenCalled();
  });

  it.each([
    ["/_next/static/chunks/app.js?dpl=synthetic", "text/javascript"],
    ["/_next/static/css/app.css", "text/css"],
  ])("serves admitted compiled asset %s without invoking Vinext", async (path, contentType) => {
    const assetResponse = new Response("asset", { headers: { "Content-Type": contentType, ETag: '"synthetic"' } });
    const assetFetch = vi.fn(async () => assetResponse);
    const response = await worker.fetch(
      new Request(`https://dev.example${path}`, { headers: {
        "CF-Connecting-IP": TEAM_IP, "X-Portfolio-Identity": "forged", "X-Portfolio-Identity-Proof": "forged",
      } }),
      { ...devEnv(), ASSETS: { fetch: assetFetch } }, ctx
    );
    expect(response).toBe(assetResponse);
    expect(response.headers.get("Content-Type")).toBe(contentType);
    expect(response.headers.get("ETag")).toBe('"synthetic"');
    expect(assetFetch).toHaveBeenCalledTimes(1);
    const [forwarded] = (assetFetch.mock.calls as unknown as [Request][])[0];
    expect(forwarded.url).toBe(`https://dev.example${path}`);
    expect(forwarded.headers.get("X-Portfolio-Identity")).toBeNull();
    expect(forwarded.headers.get("X-Portfolio-Identity-Proof")).toBeNull();
    expect(handlerFetch).not.toHaveBeenCalled();
  });

  it("preserves HEAD when dispatching an admitted asset", async () => {
    const assetFetch = vi.fn(async () => new Response(null, { headers: { "Content-Type": "text/css" } }));
    const response = await worker.fetch(
      new Request("https://dev.example/_next/static/css/app.css", { method: "HEAD", headers: { "CF-Connecting-IP": TEAM_IP } }),
      { ...devEnv(), ASSETS: { fetch: assetFetch } }, ctx
    );
    expect(response.status).toBe(200);
    expect(await response.text()).toBe("");
    expect((assetFetch.mock.calls as unknown as [Request][])[0][0].method).toBe("HEAD");
    expect(handlerFetch).not.toHaveBeenCalled();
  });

  it("preserves missing-asset 404 instead of falling back to application HTML", async () => {
    const missing = new Response("Not Found", { status: 404 });
    const response = await worker.fetch(
      new Request("https://dev.example/_next/static/missing.js", { headers: { "CF-Connecting-IP": TEAM_IP } }),
      { ...devEnv(), ASSETS: { fetch: vi.fn(async () => missing) } }, ctx
    );
    expect(response).toBe(missing);
    expect(handlerFetch).not.toHaveBeenCalled();
  });

  it.each([undefined, "[]", "not-json"])("never reaches assets with absent, empty or invalid dev allowlist %j", async (allowlist) => {
    const assetFetch = vi.fn();
    const response = await worker.fetch(
      new Request("https://dev.example/_next/static/app.js", { headers: { "CF-Connecting-IP": TEAM_IP } }),
      { ...devEnv(), TEAM_ALLOWLIST: allowlist, ASSETS: { fetch: assetFetch } }, ctx
    );
    expect(response.status).toBe(503);
    expect(assetFetch).not.toHaveBeenCalled();
    expect(handlerFetch).not.toHaveBeenCalled();
  });

  it("serves public prod compiled assets without requiring team membership", async () => {
    const assetFetch = vi.fn(async () => new Response("asset"));
    const response = await worker.fetch(
      new Request("https://portfolio.example/_next/static/app.js", { headers: { "CF-Connecting-IP": "198.51.100.8" } }),
      { PORTFOLIO_STAGE: "prod", ASSETS: { fetch: assetFetch } }, ctx
    );
    expect(await response.text()).toBe("asset");
    expect(assetFetch).toHaveBeenCalledTimes(1);
    expect(handlerFetch).not.toHaveBeenCalled();
  });

  it.each(["/", "/projects/vextis", "/api/example", "/_next/image?url=%2Ffile.svg&w=64&q=75", "/_next/static-other/app.js"])(
    "keeps non-compiled route %s in Vinext", async (path) => {
      const assetFetch = vi.fn();
      const response = await worker.fetch(
        new Request(`https://dev.example${path}`, { headers: { "CF-Connecting-IP": TEAM_IP } }),
        { ...devEnv(), ASSETS: { fetch: assetFetch } }, ctx
      );
      expect(await response.text()).toBe("app");
      expect(handlerFetch).toHaveBeenCalledTimes(1);
      expect(assetFetch).not.toHaveBeenCalled();
    }
  );

  it("does not turn POST to a compiled asset path into an asset read", async () => {
    const assetFetch = vi.fn();
    await worker.fetch(
      new Request("https://dev.example/_next/static/app.js", { method: "POST", headers: { "CF-Connecting-IP": TEAM_IP } }),
      { ...devEnv(), ASSETS: { fetch: assetFetch } }, ctx
    );
    expect(assetFetch).not.toHaveBeenCalled();
    expect(handlerFetch).toHaveBeenCalledTimes(1);
  });

  it("fails closed when the asset binding is unavailable", async () => {
    const response = await worker.fetch(
      new Request("https://dev.example/_next/static/app.js", { headers: { "CF-Connecting-IP": TEAM_IP } }), devEnv(), ctx
    );
    expect(response.status).toBe(503);
    expect(response.headers.get("Cache-Control")).toBe("private, no-store");
    expect(handlerFetch).not.toHaveBeenCalled();
  });

  it("denies when the stage is missing, without reaching the application", async () => {
    const response = await worker.fetch(new Request("https://dev.example/"), {}, ctx);
    expect(response.status).toBe(503);
    expect(handlerFetch).not.toHaveBeenCalled();
  });

  it("answers health itself without reaching the application", async () => {
    const response = await worker.fetch(new Request("https://dev.example/health"), devEnv(), ctx);
    expect(response.status).toBe(200);
    expect(handlerFetch).not.toHaveBeenCalled();
  });

  it("forwards an admitted request once, with the original env and context", async () => {
    const env = devEnv();
    const response = await worker.fetch(
      new Request("https://dev.example/", { headers: { "CF-Connecting-IP": TEAM_IP, "X-Forwarded-For": "198.51.100.8" } }),
      env,
      ctx
    );
    expect(await response.text()).toBe("app");
    expect(handlerFetch).toHaveBeenCalledTimes(1);
    const [forwarded, forwardedEnv, forwardedCtx] = (handlerFetch.mock.calls as unknown as [Request, unknown, unknown][])[0];
    expect(forwarded.headers.get("CF-Connecting-IP")).toBe(TEAM_IP);
    expect(forwardedEnv).toBe(env);
    expect(forwardedCtx).toBe(ctx);
  });

  it("refuses limited routes without the server-only key secret and never dispatches", async () => {
    const limiter = { limit: async () => ({ success: true }) };
    const response = await worker.fetch(
      new Request("https://portfolio.example/api/jobs/analyze", {
        method: "POST",
        headers: { "CF-Connecting-IP": "198.51.100.8" },
      }),
      { PORTFOLIO_STAGE: "prod", RATE_LIMIT_IP_ORDINARY: limiter, RATE_LIMIT_IP_TEAM: limiter, RATE_LIMIT_COUNTRY: limiter },
      ctx
    );
    expect(response.status).toBe(503);
    expect(handlerFetch).not.toHaveBeenCalled();
  });

  it("refuses limited routes when the rate-limit bindings are missing", async () => {
    const response = await worker.fetch(
      new Request("https://portfolio.example/api/assistant/chat/stream", {
        method: "POST",
        headers: { "CF-Connecting-IP": "198.51.100.8" },
      }),
      { PORTFOLIO_STAGE: "prod" },
      ctx
    );
    expect(response.status).toBe(503);
    expect(handlerFetch).not.toHaveBeenCalled();
  });
});
