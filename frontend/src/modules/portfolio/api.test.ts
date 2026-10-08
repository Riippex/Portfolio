import { afterEach, describe, expect, it, vi } from "vitest";
import { decodeIdentity, IDENTITY_HEADER, IDENTITY_PROOF_HEADER } from "@/modules/security/identity";
import { getProfile, getProjectBySlug, getSelectedProjects } from "./api";

const PROFILE = { name: "Rafael", headline: "h", summary: "s", evidenceStatus: "pending", focusAreas: [] };

function captureFetch(body: unknown = PROFILE, status = 200) {
  const calls: { url: string; headers: Record<string, string> }[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url: String(url), headers: init.headers as Record<string, string> });
      return new Response(JSON.stringify(body), { status });
    })
  );
  return calls;
}

describe("backend data reads", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.unstubAllGlobals();
  });

  it("signs each dev-stage read with a service identity bound to its path", async () => {
    vi.stubEnv("PORTFOLIO_STAGE", "dev");
    vi.stubEnv("ASSISTANT_PROXY_IDENTITY_SECRET", "proxy-test-secret");
    vi.stubEnv("PORTFOLIO_BACKEND_URL", "https://backend.example");
    const calls = captureFetch();

    await getProfile();
    await getSelectedProjects();

    expect(calls.map((call) => call.url)).toEqual(["https://backend.example/v1/profile", "https://backend.example/v1/projects"]);
    expect(calls.map((call) => decodeIdentity(call.headers[IDENTITY_HEADER]))).toMatchObject([
      { kind: "service-read", stage: "dev", method: "GET", path: "/v1/profile" },
      { kind: "service-read", stage: "dev", method: "GET", path: "/v1/projects" },
    ]);
    expect(calls[0].headers[IDENTITY_PROOF_HEADER]).toMatch(/^[0-9a-f]{64}$/);
  });

  it("signs the decoded project path while requesting the encoded URL", async () => {
    vi.stubEnv("PORTFOLIO_STAGE", "dev");
    vi.stubEnv("ASSISTANT_PROXY_IDENTITY_SECRET", "proxy-test-secret");
    vi.stubEnv("PORTFOLIO_BACKEND_URL", "https://backend.example");
    const calls = captureFetch({}, 404);

    const result = await getProjectBySlug("a+b");

    expect(result).toEqual({ ok: true, data: null });
    expect(calls[0].url).toBe("https://backend.example/v1/projects/a%2Bb");
    expect(decodeIdentity(calls[0].headers[IDENTITY_HEADER])).toMatchObject({ path: "/v1/projects/a+b" });
  });

  it("sends no identity in prod, where the data routes are public", async () => {
    vi.stubEnv("PORTFOLIO_STAGE", "prod");
    vi.stubEnv("PORTFOLIO_BACKEND_URL", "https://backend.example");
    const calls = captureFetch();

    const result = await getProfile();

    expect(result.ok).toBe(true);
    expect(calls[0].headers[IDENTITY_HEADER]).toBeUndefined();
    expect(calls[0].headers[IDENTITY_PROOF_HEADER]).toBeUndefined();
  });

  it("does not call the backend when the dev secret or the stage is missing", async () => {
    vi.stubEnv("PORTFOLIO_STAGE", "dev");
    vi.stubEnv("ASSISTANT_PROXY_IDENTITY_SECRET", "");
    let calls = captureFetch();
    expect((await getProfile()).ok).toBe(false);
    expect(calls).toHaveLength(0);

    vi.stubEnv("PORTFOLIO_STAGE", "");
    calls = captureFetch();
    expect((await getSelectedProjects()).ok).toBe(false);
    expect(calls).toHaveLength(0);
  });
});
