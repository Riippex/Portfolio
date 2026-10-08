import { describe, it, expect, vi, beforeEach } from "vitest";
import { canonicalizeIp, parseTeamAllowlist, isTeamMember } from "./team-allowlist";
import { buildProxyIdentityHeaders, hmacSha256Hex } from "./identity";

describe("team-allowlist", () => {
  it("canonicalizes IPv4 and IPv6 correctly", () => {
    expect(canonicalizeIp(" 192.168.1.1 ")).toBe("192.168.1.1");
    expect(canonicalizeIp("[2001:db8::1]")).toBe("2001:db8::1");
    expect(canonicalizeIp("2001:0DB8::1")).toBe("2001:0db8::1");
    expect(canonicalizeIp("invalid-ip-string")).toBe("invalid-ip-string");
  });

  it("parses JSON array or comma-separated allowlists", () => {
    const jsonParsed = parseTeamAllowlist('["1.2.3.4", "5.6.7.8"]');
    expect(jsonParsed.valid).toBe(true);
    expect(jsonParsed.ips).toEqual(["1.2.3.4", "5.6.7.8"]);

    const csvParsed = parseTeamAllowlist("10.0.0.1, 10.0.0.2");
    expect(csvParsed.valid).toBe(true);
    expect(csvParsed.ips).toEqual(["10.0.0.1", "10.0.0.2"]);
  });

  it("handles empty allowlist cleanly", () => {
    const empty = parseTeamAllowlist("");
    expect(empty.valid).toBe(true);
    expect(empty.ips).toEqual([]);
  });

  it("identifies team members correctly", () => {
    const allowlist = ["1.2.3.4", "10.0.0.1"];
    expect(isTeamMember("1.2.3.4", allowlist)).toBe(true);
    expect(isTeamMember("9.9.9.9", allowlist)).toBe(false);
    expect(isTeamMember(null, allowlist)).toBe(false);
  });
});

describe("identity", () => {
  const secret = "test-proxy-secret";

  beforeEach(() => {
    vi.stubEnv("ASSISTANT_PROXY_IDENTITY_SECRET", secret);
    vi.stubEnv("PORTFOLIO_STAGE", "prod");
    vi.stubEnv("TEAM_ALLOWLIST", "1.2.3.4");
  });

  it("computes HMAC SHA256 signature correctly", async () => {
    const hmac = await hmacSha256Hex("secret", "message");
    expect(hmac).toHaveLength(64);
  });

  it("builds proxy identity headers for team member in prod", async () => {
    const req = new Request("https://portfolio.local/api/assistant/chat", {
      headers: {
        "CF-Connecting-IP": "1.2.3.4",
        "CF-IPCountry": "CO",
      },
    });

    const { headers, error } = await buildProxyIdentityHeaders(req);
    expect(error).toBeUndefined();
    expect(headers["X-Client-Country"]).toBe("CO");
    expect(headers["X-Client-Tier"]).toBe("team");
    expect(headers["X-Client-Stage"]).toBe("prod");
    expect(headers["X-Client-Key-Proof"]).toBeDefined();
  });

  it("builds proxy identity headers for ordinary member in prod", async () => {
    const req = new Request("https://portfolio.local/api/assistant/chat", {
      headers: {
        "CF-Connecting-IP": "8.8.8.8",
        "CF-IPCountry": "US",
      },
    });

    const { headers, error } = await buildProxyIdentityHeaders(req);
    expect(error).toBeUndefined();
    expect(headers["X-Client-Country"]).toBe("US");
    expect(headers["X-Client-Tier"]).toBe("ordinary");
  });

  it("denies access in dev stage if IP is not team member", async () => {
    vi.stubEnv("PORTFOLIO_STAGE", "dev");
    const req = new Request("https://portfolio.local/api/assistant/chat", {
      headers: {
        "CF-Connecting-IP": "9.9.9.9",
      },
    });

    const { error } = await buildProxyIdentityHeaders(req);
    expect(error).toBeDefined();
    expect(error?.status).toBe(403);
  });
});
