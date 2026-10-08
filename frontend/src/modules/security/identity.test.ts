import { describe, expect, it } from "vitest";
import vectors from "../../../../docs/contracts/visitor-identity-v2.json";
import {
  buildProxyIdentityHeaders,
  buildServiceReadHeaders,
  decodeIdentity,
  encodeIdentity,
  hmacSha256Hex,
  IDENTITY_HEADER,
  IDENTITY_PROOF_DOMAIN,
  IDENTITY_PROOF_HEADER,
  signIdentity,
  type IdentityClaims,
} from "./identity";

const SECRET = "unit-proxy-secret";
const TARGET = { method: "POST", path: "/v1/assistant/chat/stream" };

const platformRequest = (headers: Record<string, string>) =>
  new Request("https://portfolio.example/api/assistant/chat/stream", { method: "POST", headers });

const env = (overrides: Record<string, string | undefined> = {}) => ({
  PORTFOLIO_STAGE: "prod",
  ASSISTANT_PROXY_IDENTITY_SECRET: SECRET,
  ...overrides,
});

describe("identity contract vectors", () => {
  it("uses the documented header names and proof domain", () => {
    expect(IDENTITY_HEADER).toBe(vectors.headers.identity);
    expect(IDENTITY_PROOF_HEADER).toBe(vectors.headers.proof);
    expect(IDENTITY_PROOF_DOMAIN).toBe(vectors.proofDomain);
  });

  it.each(vectors.identity.valid)("encodes, signs and decodes: $name", async ({ claims, identity, proof }) => {
    const decoded = decodeIdentity(identity) as IdentityClaims;
    expect(decoded).not.toBeNull();
    const expected: Record<string, unknown> = { ...claims };
    delete expected.v;
    expect(decoded).toEqual(expected);

    expect(encodeIdentity(decoded)).toBe(identity);
    expect(await signIdentity(vectors.secret, decoded)).toEqual({
      [IDENTITY_HEADER]: identity,
      [IDENTITY_PROOF_HEADER]: proof,
    });
  });

  it.each(vectors.identity.invalid)("rejects a correctly signed but invalid payload: $name", async ({ identity, proof }) => {
    expect(decodeIdentity(identity)).toBeNull();
    expect(proof).toBe(await hmacSha256Hex(vectors.secret, IDENTITY_PROOF_DOMAIN + identity));
  });
});

describe("signIdentity tamper evidence", () => {
  const claims: IdentityClaims = {
    kind: "visitor",
    ip: "203.0.113.9",
    country: "CO",
    tier: "ordinary",
    stage: "prod",
    method: "POST",
    path: "/v1/jobs/analyze",
  };

  it("changes the proof when any claim changes", async () => {
    const base = (await signIdentity(SECRET, claims))[IDENTITY_PROOF_HEADER];
    const variants: IdentityClaims[] = [
      { ...claims, ip: "203.0.113.10" },
      { ...claims, country: "US" },
      { ...claims, tier: "team" },
      { ...claims, stage: "dev" },
      { ...claims, method: "PUT" },
      { ...claims, path: "/v1/contact" },
    ];
    for (const variant of variants) {
      expect((await signIdentity(SECRET, variant))[IDENTITY_PROOF_HEADER]).not.toBe(base);
    }
  });

  it("changes the proof under a different secret and covers the domain separator", async () => {
    const signed = await signIdentity(SECRET, claims);
    expect((await signIdentity("another-secret", claims))[IDENTITY_PROOF_HEADER]).not.toBe(signed[IDENTITY_PROOF_HEADER]);
    expect(signed[IDENTITY_PROOF_HEADER]).toBe(await hmacSha256Hex(SECRET, IDENTITY_PROOF_DOMAIN + signed[IDENTITY_HEADER]));
    expect(signed[IDENTITY_PROOF_HEADER]).not.toBe(await hmacSha256Hex(SECRET, signed[IDENTITY_HEADER]!));
  });

  it("round-trips IPv4 and IPv6 visitors", async () => {
    for (const ip of ["203.0.113.9", "2001:db8::1", "64:ff9b::c000:221"]) {
      const identity = (await signIdentity(SECRET, { ...claims, ip }))[IDENTITY_HEADER]!;
      expect(decodeIdentity(identity)).toMatchObject({ ip });
    }
  });

  it("does not embed the IP in clear text or as a delimited string", async () => {
    const identity = (await signIdentity(SECRET, claims))[IDENTITY_HEADER]!;
    expect(identity).not.toContain("203.0.113.9");
    expect(identity).not.toContain(":");
  });
});

describe("buildProxyIdentityHeaders", () => {
  it("signs an ordinary prod visitor from platform metadata", async () => {
    const { headers, error } = await buildProxyIdentityHeaders(
      platformRequest({ "CF-Connecting-IP": "203.0.113.9", "CF-IPCountry": "CO" }),
      TARGET,
      env()
    );
    expect(error).toBeUndefined();
    expect(decodeIdentity(headers[IDENTITY_HEADER]!)).toEqual({
      kind: "visitor",
      ip: "203.0.113.9",
      country: "CO",
      tier: "ordinary",
      stage: "prod",
      method: "POST",
      path: "/v1/assistant/chat/stream",
    });
  });

  it("marks an allowlisted visitor as team without bypassing anything else", async () => {
    const { headers } = await buildProxyIdentityHeaders(
      platformRequest({ "CF-Connecting-IP": "2001:DB8:0:0:0:0:0:1", "CF-IPCountry": "US" }),
      TARGET,
      env({ TEAM_ALLOWLIST: '["2001:db8::1"]' })
    );
    expect(decodeIdentity(headers[IDENTITY_HEADER]!)).toMatchObject({ tier: "team", ip: "2001:db8::1" });
  });

  it("binds the claims to the stage, method and path of the call", async () => {
    const { headers } = await buildProxyIdentityHeaders(
      platformRequest({ "CF-Connecting-IP": "203.0.113.9" }),
      { method: "POST", path: "/v1/contact" },
      env()
    );
    expect(decodeIdentity(headers[IDENTITY_HEADER]!)).toMatchObject({ method: "POST", path: "/v1/contact", stage: "prod" });
  });

  it.each([undefined, "", "development", "production", "DEV", "staging"])(
    "fails closed with 503 for stage %j, not inferring one from NODE_ENV",
    async (stage) => {
      const { headers, error } = await buildProxyIdentityHeaders(
        platformRequest({ "CF-Connecting-IP": "203.0.113.9" }),
        TARGET,
        { ...env({ PORTFOLIO_STAGE: stage }), NODE_ENV: "development" }
      );
      expect(error?.status).toBe(503);
      expect(headers).toEqual({});
    }
  );

  it("fails closed in dev without a usable allowlist", async () => {
    for (const TEAM_ALLOWLIST of [undefined, "", "[]", "203.0.113.9", '["203.0.113.0/24"]']) {
      const { headers, error } = await buildProxyIdentityHeaders(
        platformRequest({ "CF-Connecting-IP": "203.0.113.9" }),
        TARGET,
        env({ PORTFOLIO_STAGE: "dev", TEAM_ALLOWLIST })
      );
      expect(error?.status).toBe(503);
      expect(headers).toEqual({});
    }
  });

  it("denies a non-allowlisted visitor in dev", async () => {
    const { headers, error } = await buildProxyIdentityHeaders(
      platformRequest({ "CF-Connecting-IP": "198.51.100.9" }),
      TARGET,
      env({ PORTFOLIO_STAGE: "dev", TEAM_ALLOWLIST: '["203.0.113.9"]' })
    );
    expect(error?.status).toBe(403);
    expect(headers).toEqual({});
  });

  it("treats an invalid allowlist as a configuration error in prod rather than ignoring it", async () => {
    const { error } = await buildProxyIdentityHeaders(
      platformRequest({ "CF-Connecting-IP": "203.0.113.9" }),
      TARGET,
      env({ TEAM_ALLOWLIST: "203.0.113.9,198.51.100.1" })
    );
    expect(error?.status).toBe(503);
  });

  it("requires the shared secret outside local development", async () => {
    for (const stage of ["dev", "prod"]) {
      const { error } = await buildProxyIdentityHeaders(
        platformRequest({ "CF-Connecting-IP": "203.0.113.9" }),
        TARGET,
        env({ PORTFOLIO_STAGE: stage, TEAM_ALLOWLIST: '["203.0.113.9"]', ASSISTANT_PROXY_IDENTITY_SECRET: "" })
      );
      expect(error?.status).toBe(503);
    }
  });

  it("does not fall back to forwarding headers when the platform IP is missing or invalid", async () => {
    const forwarded = {
      "X-Forwarded-For": "198.51.100.7",
      Forwarded: "for=198.51.100.7",
      "X-Real-IP": "198.51.100.7",
      "True-Client-IP": "198.51.100.7",
    };
    const platforms: Record<string, string>[] = [{}, { "CF-Connecting-IP": "not-an-ip" }, { "CF-Connecting-IP": "198.51.100.7:443" }];
    for (const platform of platforms) {
      const { headers, error } = await buildProxyIdentityHeaders(platformRequest({ ...forwarded, ...platform }), TARGET, env());
      expect(error?.status).toBe(403);
      expect(headers).toEqual({});
    }
  });

  it("lets local development proceed unsigned without a platform address", async () => {
    const { headers, error } = await buildProxyIdentityHeaders(
      platformRequest({ "X-Forwarded-For": "198.51.100.7" }),
      TARGET,
      { PORTFOLIO_STAGE: "local" }
    );
    expect(error).toBeUndefined();
    expect(headers).toEqual({});
  });

  it("ignores caller-supplied identity headers", async () => {
    const { headers } = await buildProxyIdentityHeaders(
      platformRequest({
        "CF-Connecting-IP": "203.0.113.9",
        [IDENTITY_HEADER]: "forged",
        [IDENTITY_PROOF_HEADER]: "forged",
        "X-Client-Tier": "team",
      }),
      TARGET,
      env()
    );
    expect(decodeIdentity(headers[IDENTITY_HEADER]!)).toMatchObject({ tier: "ordinary" });
    expect(headers[IDENTITY_PROOF_HEADER]).toMatch(/^[0-9a-f]{64}$/);
  });
});

describe("buildServiceReadHeaders", () => {
  it("signs a time-bound GET read for the private dev stage", async () => {
    const { headers, error } = await buildServiceReadHeaders("/v1/profile", env({ PORTFOLIO_STAGE: "dev" }), 1_790_000_000_999);
    expect(error).toBeUndefined();
    expect(decodeIdentity(headers[IDENTITY_HEADER]!)).toEqual({
      kind: "service-read",
      stage: "dev",
      method: "GET",
      path: "/v1/profile",
      iat: 1_790_000_000,
    });
    expect(headers[IDENTITY_PROOF_HEADER]).toBe(await hmacSha256Hex(SECRET, IDENTITY_PROOF_DOMAIN + headers[IDENTITY_HEADER]));
  });

  it("sends nothing in prod or local, where data routes are public", async () => {
    expect(await buildServiceReadHeaders("/v1/profile", env())).toEqual({ headers: {} });
    expect(await buildServiceReadHeaders("/v1/profile", { PORTFOLIO_STAGE: "local" })).toEqual({ headers: {} });
  });

  it("fails closed in dev without the secret or with no stage", async () => {
    const noSecret = await buildServiceReadHeaders("/v1/profile", env({ PORTFOLIO_STAGE: "dev", ASSISTANT_PROXY_IDENTITY_SECRET: "" }));
    expect(noSecret.error?.status).toBe(503);
    const noStage = await buildServiceReadHeaders("/v1/profile", { ASSISTANT_PROXY_IDENTITY_SECRET: SECRET });
    expect(noStage.error?.status).toBe(503);
  });
});
