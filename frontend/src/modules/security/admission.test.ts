import { describe, expect, it, vi } from "vitest";
import {
  admitRequest,
  EDGE_KEY_DOMAIN,
  edgeCounterKey,
  EDGE_LIMITS,
  policyPath,
  type AdmissionDecision,
  type AdmissionEnv,
  type RateLimitBinding,
} from "./admission";
import { hmacSha256Hex, IDENTITY_HEADER, IDENTITY_PROOF_HEADER } from "./identity";

const TEAM_IP = "203.0.113.4";
const OTHER_IP = "198.51.100.8";
const ALLOWLIST = JSON.stringify([TEAM_IP]);

type Responded = Extract<AdmissionDecision, { action: "respond" }>;
type Forwarded = Extract<AdmissionDecision, { action: "forward" }>;

function request(path: string, headers: Record<string, string> = {}, method = "GET", cfCountry?: string) {
  const built = new Request(`https://portfolio.example${path}`, { method, headers });
  if (cfCountry) Object.defineProperty(built, "cf", { value: { country: cfCountry } });
  return built;
}

function fakeLimiter(limit: number): RateLimitBinding & { calls: string[] } {
  const counts = new Map<string, number>();
  const calls: string[] = [];
  return {
    calls,
    async limit({ key }) {
      calls.push(key);
      const next = (counts.get(key) ?? 0) + 1;
      counts.set(key, next);
      return { success: next <= limit };
    },
  };
}

function bindings() {
  return {
    RATE_LIMIT_IP_ORDINARY: fakeLimiter(EDGE_LIMITS.ordinaryIpPerMinute),
    RATE_LIMIT_IP_TEAM: fakeLimiter(EDGE_LIMITS.teamIpPerMinute),
    RATE_LIMIT_COUNTRY: fakeLimiter(EDGE_LIMITS.countryPerMinute),
  };
}

const EDGE_SECRET = "edge-test-secret";
const prodEnv = (extra: Partial<AdmissionEnv> = {}): AdmissionEnv => ({
  PORTFOLIO_STAGE: "prod",
  ASSISTANT_PROXY_IDENTITY_SECRET: EDGE_SECRET,
  ...bindings(),
  ...extra,
});
const devEnv = (extra: Partial<AdmissionEnv> = {}): AdmissionEnv => ({
  PORTFOLIO_STAGE: "dev",
  TEAM_ALLOWLIST: ALLOWLIST,
  ASSISTANT_PROXY_IDENTITY_SECRET: EDGE_SECRET,
  ...bindings(),
  ...extra,
});

const statusOf = (decision: AdmissionDecision) => (decision.action === "respond" ? decision.response.status : 0);
const forwarded = (decision: AdmissionDecision) => {
  expect(decision.action).toBe("forward");
  return (decision as Forwarded).request;
};
const apiPost = (ip: string, country = "CO") =>
  request("/api/assistant/chat/stream", { "CF-Connecting-IP": ip }, "POST", country);
const counterOf = (limiter: unknown) => (limiter as ReturnType<typeof fakeLimiter>).calls;

describe("health", () => {
  it("answers exactly GET and HEAD /health without content, before any configuration check", async () => {
    for (const env of [{}, { PORTFOLIO_STAGE: "bogus" }, devEnv(), prodEnv()]) {
      const get = await admitRequest(request("/health"), env);
      expect(statusOf(get)).toBe(200);
      expect(JSON.parse(await (get as Responded).response.text())).toEqual({ status: "ok" });
      expect(statusOf(await admitRequest(request("/health", {}, "HEAD"), env))).toBe(200);
    }
  });

  it.each(["/health/", "/health/x", "/HEALTH", "/api/health", "/healthz", "/health.json", "/%68ealth"])(
    "does not make %s public in dev",
    async (path) => {
      expect(statusOf(await admitRequest(request(path, { "CF-Connecting-IP": OTHER_IP }), devEnv()))).toBe(403);
    }
  );

  it("does not expose health for other methods", async () => {
    expect(statusOf(await admitRequest(request("/health", { "CF-Connecting-IP": OTHER_IP }, "POST"), devEnv()))).toBe(403);
  });
});

describe("stage configuration", () => {
  it.each([undefined, "", "local", "development", "production", "DEV", "staging"])(
    "fails closed with 503 for stage %j",
    async (stage) => {
      const decision = await admitRequest(request("/", { "CF-Connecting-IP": TEAM_IP }), { PORTFOLIO_STAGE: stage });
      expect(statusOf(decision)).toBe(503);
    }
  );

  it("does not infer the stage from NODE_ENV or other variables", async () => {
    const env = { NODE_ENV: "development", STAGE: "dev", NEXT_PUBLIC_APP_STAGE: "dev" } as unknown as AdmissionEnv;
    expect(statusOf(await admitRequest(request("/", { "CF-Connecting-IP": TEAM_IP }), env))).toBe(503);
  });
});

describe("private dev admission", () => {
  const pagePaths = [
    "/",
    "/projects/example",
    "/_next/static/chunks/app.js",
    "/_next/image?url=%2Fnext.svg&w=64&q=75",
    "/_vinext/image?url=%2Fnext.svg&w=64&q=75",
    "/favicon.ico",
    "/file.svg",
    "/vinext-client-entry-manifest.json",
    "/.well-known/anything",
  ];
  const paths = [...pagePaths, "/api/assistant/chat/stream", "/api/jobs/analyze", "/api/contact", "/api/unknown"];

  it.each(paths)("denies a non-allowlisted visitor for %s", async (path) => {
    const decision = await admitRequest(request(path, { "CF-Connecting-IP": OTHER_IP }, "GET", "US"), devEnv());
    expect(statusOf(decision)).toBe(403);
    const response = (decision as Responded).response;
    expect(response.headers.get("Cache-Control")).toBe("private, no-store");
    expect(await response.text()).not.toContain(OTHER_IP);
  });

  it.each(paths)("denies a request without a platform IP for %s", async (path) => {
    const decision = await admitRequest(request(path, { "X-Forwarded-For": TEAM_IP, "X-Real-IP": TEAM_IP }), devEnv());
    expect(statusOf(decision)).toBe(403);
  });

  it("denies a caller who spoofs forwarding headers with an allowlisted address", async () => {
    const spoofed = request("/", {
      "CF-Connecting-IP": OTHER_IP,
      "X-Forwarded-For": TEAM_IP,
      Forwarded: `for=${TEAM_IP}`,
      "True-Client-IP": TEAM_IP,
    });
    expect(statusOf(await admitRequest(spoofed, devEnv()))).toBe(403);
  });

  it.each(pagePaths)("admits an allowlisted visitor for %s", async (path) => {
    const decision = await admitRequest(request(path, { "CF-Connecting-IP": TEAM_IP }), devEnv());
    expect(decision.action).toBe("forward");
  });

  it("admits equivalent spellings of an allowlisted IPv6 address", async () => {
    const env = devEnv({ TEAM_ALLOWLIST: '["2001:DB8::1"]' });
    const decision = await admitRequest(request("/", { "CF-Connecting-IP": "2001:0db8:0:0:0:0:0:1" }), env);
    expect(forwarded(decision).headers.get("CF-Connecting-IP")).toBe("2001:db8::1");
  });

  it.each([
    ["missing", undefined],
    ["empty string", ""],
    ["empty array", "[]"],
    ["comma list", TEAM_IP],
    ["CIDR", '["203.0.113.0/24"]'],
    ["malformed", "["],
  ])("fails closed with 503 when the dev allowlist is %s", async (_name, TEAM_ALLOWLIST) => {
    const decision = await admitRequest(request("/", { "CF-Connecting-IP": TEAM_IP }), devEnv({ TEAM_ALLOWLIST }));
    expect(statusOf(decision)).toBe(503);
  });
});

describe("public prod admission", () => {
  it("serves pages and assets to any visitor, including without a platform IP", async () => {
    for (const path of ["/", "/_next/static/chunks/app.js", "/favicon.ico", "/projects/example"]) {
      expect((await admitRequest(request(path), prodEnv())).action).toBe("forward");
    }
  });

  it("does not require the allowlist secret for public pages", async () => {
    const decision = await admitRequest(request("/", { "CF-Connecting-IP": OTHER_IP }), prodEnv({ TEAM_ALLOWLIST: undefined }));
    expect(decision.action).toBe("forward");
  });
});

describe("request normalization", () => {
  it("replaces caller-controlled identity with canonical platform values", async () => {
    const decision = await admitRequest(
      request(
        "/",
        {
          "CF-Connecting-IP": "::ffff:203.0.113.77",
          "CF-IPCountry": "ZZZ",
          [IDENTITY_HEADER]: "forged",
          [IDENTITY_PROOF_HEADER]: "forged",
        },
        "GET",
        "co"
      ),
      prodEnv()
    );
    const next = forwarded(decision);
    expect(next.headers.get("CF-Connecting-IP")).toBe("203.0.113.77");
    expect(next.headers.get("CF-IPCountry")).toBe("CO");
    expect(next.headers.get(IDENTITY_HEADER)).toBeNull();
    expect(next.headers.get(IDENTITY_PROOF_HEADER)).toBeNull();
  });

  it("removes platform headers it cannot validate", async () => {
    const next = forwarded(await admitRequest(request("/", { "CF-Connecting-IP": "not-an-ip", "CF-IPCountry": "CO" }), prodEnv()));
    expect(next.headers.get("CF-Connecting-IP")).toBeNull();
    expect(next.headers.get("CF-IPCountry")).toBeNull();
  });

  it("preserves method, URL and body", async () => {
    const original = new Request("https://portfolio.example/api/contact?x=1", {
      method: "POST",
      headers: { "CF-Connecting-IP": OTHER_IP, "Content-Type": "application/json" },
      body: '{"a":1}',
    });
    const next = forwarded(await admitRequest(original, prodEnv()));
    expect(next.method).toBe("POST");
    expect(next.url).toBe("https://portfolio.example/api/contact?x=1");
    expect(await next.text()).toBe('{"a":1}');
  });
});

describe("edge rate limits", () => {
  it("documents the approved numbers", () => {
    expect(EDGE_LIMITS).toEqual({ ordinaryIpPerMinute: 5, teamIpPerMinute: 15, countryPerMinute: 100 });
  });

  it("allows an ordinary IP 5 requests per minute and refuses the sixth", async () => {
    const env = prodEnv();
    for (let i = 0; i < 5; i++) {
      expect((await admitRequest(apiPost(OTHER_IP), env)).action).toBe("forward");
    }
    const refused = await admitRequest(apiPost(OTHER_IP), env);
    expect(statusOf(refused)).toBe(429);
    expect((refused as Responded).response.headers.get("Retry-After")).toBe("60");
  });

  it("allows a team IP 15 requests per minute and refuses the sixteenth", async () => {
    const env = prodEnv({ TEAM_ALLOWLIST: ALLOWLIST });
    for (let i = 0; i < 15; i++) {
      expect((await admitRequest(apiPost(TEAM_IP), env)).action).toBe("forward");
    }
    expect(statusOf(await admitRequest(apiPost(TEAM_IP), env))).toBe(429);
  });

  it("shares one quota between chat and job analysis", async () => {
    const env = prodEnv();
    for (let i = 0; i < 3; i++) await admitRequest(apiPost(OTHER_IP), env);
    for (let i = 0; i < 2; i++) {
      const job = request("/api/jobs/analyze", { "CF-Connecting-IP": OTHER_IP }, "POST", "CO");
      expect((await admitRequest(job, env)).action).toBe("forward");
    }
    expect(statusOf(await admitRequest(apiPost(OTHER_IP), env))).toBe(429);
  });

  it("counts team traffic toward the country limit and refuses the 101st request", async () => {
    const team = Array.from({ length: 10 }, (_, i) => `203.0.113.${i + 1}`);
    const env = prodEnv({ TEAM_ALLOWLIST: JSON.stringify(team) });
    let admitted = 0;
    for (let i = 0; i < 100; i++) {
      if ((await admitRequest(apiPost(team[i % 10], "BR"), env)).action === "forward") admitted++;
    }
    expect(admitted).toBe(100);
    expect(statusOf(await admitRequest(apiPost(team[0], "BR"), env))).toBe(429);
    expect((await admitRequest(apiPost(team[0], "MX"), env)).action).toBe("forward");
  });

  it("does not spend country capacity on a request refused for its own IP", async () => {
    const env = prodEnv();
    for (let i = 0; i < 8; i++) await admitRequest(apiPost(OTHER_IP, "AR"), env);
    expect(counterOf(env.RATE_LIMIT_COUNTRY)).toHaveLength(5);
  });

  it("applies the same edge limits in dev for admitted visitors", async () => {
    const env = devEnv();
    for (let i = 0; i < 15; i++) {
      expect((await admitRequest(apiPost(TEAM_IP), env)).action).toBe("forward");
    }
    expect(statusOf(await admitRequest(apiPost(TEAM_IP), env))).toBe(429);
  });

  it("keys by canonical address so IPv6 spellings share one counter", async () => {
    const env = prodEnv();
    const spellings = ["2001:db8::1", "2001:DB8:0:0:0:0:0:1", "2001:0db8::0001", "2001:db8:0:0:0:0:0:1", "2001:DB8::1", "2001:db8::1"];
    const results: string[] = [];
    for (const ip of spellings) results.push((await admitRequest(apiPost(ip), env)).action);
    expect(results).toEqual(["forward", "forward", "forward", "forward", "forward", "respond"]);
  });

  it.each([
    "/api/assistant/chat/stream/",
    "/API/Assistant/Chat/Stream",
    "/api//assistant/chat/stream",
    "/api/assistant/chat/%73tream",
    "/api/assistant/chat/%2573tream",
    "/api/jobs/analyze/",
  ])("cannot be bypassed with the path spelling %s", async (path) => {
    expect(policyPath(path)).toMatch(/^\/api\/(assistant\/chat\/stream|jobs\/analyze)$/);
    const env = prodEnv();
    for (let i = 0; i < 5; i++) {
      await admitRequest(request(path, { "CF-Connecting-IP": OTHER_IP }, "POST", "CO"), env);
    }
    expect(statusOf(await admitRequest(request(path, { "CF-Connecting-IP": OTHER_IP }, "POST", "CO"), env))).toBe(429);
  });

  it("does not apply the edge limit to other routes", async () => {
    const env = prodEnv();
    for (let i = 0; i < 12; i++) {
      expect((await admitRequest(request("/api/contact", { "CF-Connecting-IP": OTHER_IP }, "POST"), env)).action).toBe("forward");
    }
  });

  it("fails closed with 503 when a rate-limit binding is missing", async () => {
    for (const missing of ["RATE_LIMIT_IP_ORDINARY", "RATE_LIMIT_COUNTRY"] as const) {
      const env = prodEnv({ [missing]: undefined });
      expect(statusOf(await admitRequest(apiPost(OTHER_IP), env))).toBe(503);
    }
    const teamEnv = prodEnv({ TEAM_ALLOWLIST: ALLOWLIST, RATE_LIMIT_IP_TEAM: undefined });
    expect(statusOf(await admitRequest(apiPost(TEAM_IP), teamEnv))).toBe(503);
  });

  it("fails closed with 503 when a binding throws", async () => {
    const throwing: RateLimitBinding = {
      limit: vi.fn(async () => {
        throw new Error("binding unavailable");
      }),
    };
    expect(statusOf(await admitRequest(apiPost(OTHER_IP), prodEnv({ RATE_LIMIT_IP_ORDINARY: throwing })))).toBe(503);
  });

  it("refuses a limited route without a platform IP instead of using a shared key", async () => {
    const env = prodEnv();
    const noIp = request("/api/assistant/chat/stream", { "X-Forwarded-For": OTHER_IP }, "POST");
    expect(statusOf(await admitRequest(noIp, env))).toBe(403);
    expect(counterOf(env.RATE_LIMIT_IP_ORDINARY)).toHaveLength(0);
  });

  it("treats an invalid allowlist as a configuration error on limited routes in prod", async () => {
    expect(statusOf(await admitRequest(apiPost(OTHER_IP), prodEnv({ TEAM_ALLOWLIST: TEAM_IP })))).toBe(503);
  });

  it("keeps raw addresses out of every refusal body", async () => {
    const env = prodEnv();
    for (let i = 0; i < 6; i++) await admitRequest(apiPost(OTHER_IP), env);
    const refused = (await admitRequest(apiPost(OTHER_IP), env)) as Responded;
    expect(await refused.response.text()).not.toContain(OTHER_IP);
  });
});

describe("edge counter pseudonyms", () => {
  const allKeys = (env: AdmissionEnv) =>
    [env.RATE_LIMIT_IP_ORDINARY, env.RATE_LIMIT_IP_TEAM, env.RATE_LIMIT_COUNTRY].flatMap((limiter) => counterOf(limiter));

  it("never gives a binding a raw address or an allowlist value", async () => {
    const team = ["203.0.113.4", "2001:db8::1"];
    const env = prodEnv({ TEAM_ALLOWLIST: JSON.stringify(team) });
    const visitors = [...team, "198.51.100.8", "2001:db8:ffff::9", "::ffff:198.51.100.9"];
    for (const ip of visitors) await admitRequest(apiPost(ip, "CO"), env);

    const keys = allKeys(env);
    expect(keys.length).toBe(visitors.length * 2);
    for (const key of keys) {
      expect(key).toMatch(/^(ip:[0-9a-f]{64}|country:[A-Z0-9]{2})$/);
    }
    const joined = keys.join("|");
    for (const secret of [...visitors, ...team, "198.51.100.9", "2001:db8", "203.0.113", EDGE_SECRET, ALLOWLIST]) {
      expect(joined).not.toContain(secret);
    }
  });

  it("uses a stable pseudonym that is the same for chat and job analysis", async () => {
    const env = prodEnv();
    await admitRequest(apiPost(OTHER_IP), env);
    await admitRequest(request("/api/jobs/analyze", { "CF-Connecting-IP": OTHER_IP }, "POST", "CO"), env);
    await admitRequest(apiPost(OTHER_IP), env);

    const ipKeys = counterOf(env.RATE_LIMIT_IP_ORDINARY);
    expect(ipKeys).toHaveLength(3);
    expect(new Set(ipKeys).size).toBe(1);
    expect(ipKeys[0]).toBe(await edgeCounterKey(EDGE_SECRET, "prod", OTHER_IP));
  });

  it("is domain separated, stage bound and secret keyed", async () => {
    const base = await edgeCounterKey(EDGE_SECRET, "prod", OTHER_IP);
    expect(await edgeCounterKey(EDGE_SECRET, "dev", OTHER_IP)).not.toBe(base);
    expect(await edgeCounterKey("another-secret", "prod", OTHER_IP)).not.toBe(base);
    expect(await edgeCounterKey(EDGE_SECRET, "prod", "198.51.100.9")).not.toBe(base);
    expect(base).not.toBe(`ip:${await hmacSha256Hex(EDGE_SECRET, OTHER_IP)}`);
    expect(base).not.toBe(`ip:${await hmacSha256Hex(EDGE_SECRET, `prod\nip\n${OTHER_IP}`)}`);
    expect(EDGE_KEY_DOMAIN).toBe("portfolio-edge-limit-v1\n");
  });

  it("counts equivalent IPv6 spellings under one pseudonym", async () => {
    const env = prodEnv();
    for (const ip of ["2001:db8::1", "2001:DB8:0:0:0:0:0:1", "2001:0db8::0001"]) await admitRequest(apiPost(ip), env);

    expect(new Set(counterOf(env.RATE_LIMIT_IP_ORDINARY)).size).toBe(1);
  });

  it("keeps the pseudonym separate per stage so dev and prod never share a counter", async () => {
    const dev = devEnv();
    const prod = prodEnv({ TEAM_ALLOWLIST: ALLOWLIST });
    await admitRequest(apiPost(TEAM_IP), dev);
    await admitRequest(apiPost(TEAM_IP), prod);

    expect(counterOf(dev.RATE_LIMIT_IP_TEAM)[0]).not.toBe(counterOf(prod.RATE_LIMIT_IP_TEAM)[0]);
  });

  it("keeps the 5 and 15 per minute limits with pseudonymous keys", async () => {
    const ordinary = prodEnv();
    for (let i = 0; i < 5; i++) expect((await admitRequest(apiPost(OTHER_IP), ordinary)).action).toBe("forward");
    expect(statusOf(await admitRequest(apiPost(OTHER_IP), ordinary))).toBe(429);

    const team = prodEnv({ TEAM_ALLOWLIST: ALLOWLIST });
    for (let i = 0; i < 15; i++) expect((await admitRequest(apiPost(TEAM_IP), team)).action).toBe("forward");
    expect(statusOf(await admitRequest(apiPost(TEAM_IP), team))).toBe(429);
  });

  it.each([undefined, "", "   ", 42, null])("fails closed with 503 and no counting when the secret is %j", async (secret) => {
    const env = prodEnv({ ASSISTANT_PROXY_IDENTITY_SECRET: secret });
    const decision = await admitRequest(apiPost(OTHER_IP), env);

    expect(decision.action).toBe("respond");
    expect(statusOf(decision)).toBe(503);
    expect(allKeys(env)).toHaveLength(0);
    expect(await (decision as Responded).response.text()).not.toContain(OTHER_IP);
  });

  it("does not require the secret for requests that are not edge limited", async () => {
    const env = prodEnv({ ASSISTANT_PROXY_IDENTITY_SECRET: undefined });
    expect((await admitRequest(request("/", { "CF-Connecting-IP": OTHER_IP }), env)).action).toBe("forward");
  });

  it("fails closed when the pseudonym cannot be computed", async () => {
    const env = prodEnv();
    const original = crypto.subtle.importKey;
    crypto.subtle.importKey = (async () => {
      throw new Error("crypto unavailable");
    }) as typeof crypto.subtle.importKey;
    try {
      expect(statusOf(await admitRequest(apiPost(OTHER_IP), env))).toBe(503);
      expect(allKeys(env)).toHaveLength(0);
    } finally {
      crypto.subtle.importKey = original;
    }
  });
});
