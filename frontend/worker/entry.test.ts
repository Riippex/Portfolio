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
    for (const path of ["/", "/_next/static/chunks/app.js", "/_next/image?url=%2Fx&w=64&q=75", "/favicon.ico", "/api/jobs/analyze"]) {
      const response = await worker.fetch(
        new Request(`https://dev.example${path}`, { headers: { "CF-Connecting-IP": "198.51.100.8" } }),
        devEnv(),
        ctx
      );
      expect(response.status).toBe(403);
    }
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
