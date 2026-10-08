import { describe, expect, it } from "vitest";
import vectors from "../../../../docs/contracts/visitor-identity-v2.json";
import { canonicalizeIp } from "./ip";

describe("canonicalizeIp contract vectors", () => {
  it.each(vectors.ip.canonical)("canonicalizes $input to $canonical", ({ input, canonical }) => {
    expect(canonicalizeIp(input)).toBe(canonical);
  });

  it.each(vectors.ip.invalid)("rejects %j", (input) => {
    expect(canonicalizeIp(input)).toBeNull();
  });

  it("is idempotent for every canonical vector", () => {
    for (const { canonical } of vectors.ip.canonical) {
      expect(canonicalizeIp(canonical)).toBe(canonical);
    }
  });

  it("treats every spelling of one IPv6 address as one identity", () => {
    const spellings = [
      "2001:db8::1",
      "2001:DB8::1",
      "2001:0db8:0:0:0:0:0:1",
      "2001:0DB8:0000:0000:0000:0000:0000:0001",
    ];
    expect(new Set(spellings.map(canonicalizeIp)).size).toBe(1);
  });

  it("keeps IPv4 and its IPv4-mapped IPv6 spelling as one identity", () => {
    expect(canonicalizeIp("::ffff:198.51.100.7")).toBe(canonicalizeIp("198.51.100.7"));
  });

  it.each([undefined, null, 4, {}, [], ["1.2.3.4"]])("rejects non-string input %j", (input) => {
    expect(canonicalizeIp(input)).toBeNull();
  });

  it("rejects over-long input", () => {
    expect(canonicalizeIp("1".repeat(300))).toBeNull();
  });
});
