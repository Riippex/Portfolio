import { afterEach, describe, expect, it, vi } from "vitest";
import { decodeIdentity, IDENTITY_HEADER, IDENTITY_PROOF_HEADER } from "@/modules/security/identity";
import {
  getProfile,
  getProjectBySlug,
  getSelectedProjects,
  isProfile,
  isProfileLink,
  isProfileLinkList,
  MAX_PROFILE_LINK_URL_LENGTH,
  sanitizeProfileLinks,
} from "./api";

const LINKEDIN_URL = "https://co.linkedin.com/in/rafael-pati%C3%B1o-diaz";
const GITHUB_URL = "https://github.com/Riippex";

const PROFILE = {
  name: "Rafael",
  headline: "h",
  summary: "s",
  evidenceStatus: "pending",
  focusAreas: [],
};

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

  it("accepts canonical LinkedIn and GitHub profile links and normalizes missing/empty links", async () => {
    vi.stubEnv("PORTFOLIO_STAGE", "prod");
    vi.stubEnv("PORTFOLIO_BACKEND_URL", "https://backend.example");

    captureFetch({
      ...PROFILE,
      evidenceStatus: "verified",
      links: [
        { platform: "linkedin", url: LINKEDIN_URL },
        { platform: "github", url: GITHUB_URL },
      ],
    });
    const withLinks = await getProfile();
    expect(withLinks).toEqual({
      ok: true,
      data: {
        ...PROFILE,
        evidenceStatus: "verified",
        links: [
          { platform: "linkedin", url: LINKEDIN_URL },
          { platform: "github", url: GITHUB_URL },
        ],
      },
    });

    captureFetch(PROFILE);
    const missingLinks = await getProfile();
    expect(missingLinks).toEqual({
      ok: true,
      data: { ...PROFILE, links: [] },
    });

    captureFetch({ ...PROFILE, links: [] });
    const emptyLinks = await getProfile();
    expect(emptyLinks).toEqual({
      ok: true,
      data: { ...PROFILE, links: [] },
    });
  });

  it.each([
    ["javascript scheme", { platform: "github", url: "javascript:alert(1)" }],
    ["data scheme", { platform: "github", url: "data:text/html,<script>alert(1)</script>" }],
    ["plain http scheme", { platform: "github", url: "http://github.com/Riippex" }],
    ["userinfo host confusion", { platform: "github", url: "https://github.com@evil.example/Riippex" }],
    ["deceptive github subdomain", { platform: "github", url: "https://github.com.evil.example/Riippex" }],
    ["deceptive linkedin host", { platform: "linkedin", url: "https://co.linkedin.com.evil.example/in/rafael" }],
    ["platform host mismatch", { platform: "github", url: LINKEDIN_URL }],
    ["injected markup in path", { platform: "github", url: 'https://github.com/Riippex"><script>alert(1)</script>' }],
    ["encoded slash in linkedin handle", { platform: "linkedin", url: "https://co.linkedin.com/in/rafael%2fadmin" }],
    ["query string", { platform: "github", url: "https://github.com/Riippex?tab=repositories" }],
    ["fragment", { platform: "github", url: "https://github.com/Riippex#readme" }],
    ["github repository subpath", { platform: "github", url: "https://github.com/Riippex/Portfolio" }],
    ["unsupported platform", { platform: "twitter", url: "https://twitter.com/example" }],
    [
      "oversized url",
      {
        platform: "linkedin",
        url: `https://co.linkedin.com/in/${"a".repeat(MAX_PROFILE_LINK_URL_LENGTH)}`,
      },
    ],
  ])("rejects unsafe or malformed link (%s)", async (_label, badLink) => {
    vi.stubEnv("PORTFOLIO_STAGE", "prod");
    vi.stubEnv("PORTFOLIO_BACKEND_URL", "https://backend.example");

    expect(isProfileLink(badLink)).toBe(false);
    expect(isProfile({ ...PROFILE, links: [badLink] })).toBe(false);
    expect(sanitizeProfileLinks([badLink])).toEqual([]);

    captureFetch({ ...PROFILE, links: [badLink] });
    const result = await getProfile();
    expect(result).toEqual({
      ok: false,
      error: "Backend response did not match Profile schema",
    });
  });

  it("rejects duplicate link platforms in schema check and deduplicates in sanitizer", () => {
    const duplicate = [
      { platform: "github", url: GITHUB_URL },
      { platform: "github", url: "https://github.com/OtherUser" },
    ];

    expect(isProfileLinkList(duplicate)).toBe(false);
    expect(isProfile({ ...PROFILE, links: duplicate })).toBe(false);
    expect(sanitizeProfileLinks(duplicate)).toEqual([
      { platform: "github", url: GITHUB_URL },
    ]);
  });
});
