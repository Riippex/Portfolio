import { describe, expect, it } from "vitest";
import { isTeamMember, MAX_ALLOWLIST_ENTRIES, parseTeamAllowlist } from "./team-allowlist";

const ok = (ips: string[]) => ({ status: "ok", ips });

describe("parseTeamAllowlist", () => {
  it("accepts a JSON array of exact IPs", () => {
    expect(parseTeamAllowlist('["203.0.113.4","198.51.100.8"]')).toEqual(ok(["203.0.113.4", "198.51.100.8"]));
  });

  it("accepts an empty array as configured but empty", () => {
    expect(parseTeamAllowlist("[]")).toEqual(ok([]));
  });

  it.each([undefined, null, "", ])("treats %j as absent", (raw) => {
    expect(parseTeamAllowlist(raw)).toEqual({ status: "absent" });
  });

  it("normalizes equivalent IPv6 spellings to one entry", () => {
    expect(parseTeamAllowlist('["2001:DB8::1","2001:0db8:0:0:0:0:0:1"]')).toEqual(ok(["2001:db8::1"]));
  });

  it("normalizes IPv4-mapped IPv6 to the IPv4 form", () => {
    expect(parseTeamAllowlist('["::ffff:203.0.113.4"]')).toEqual(ok(["203.0.113.4"]));
  });

  it.each([
    ["comma list", "203.0.113.4,198.51.100.8"],
    ["space list", "203.0.113.4 198.51.100.8"],
    ["bare address", "203.0.113.4"],
    ["JSON object", '{"ips":["203.0.113.4"]}'],
    ["JSON string", '"203.0.113.4"'],
    ["truncated JSON", '["203.0.113.4"'],
    ["CIDR", '["203.0.113.0/24"]'],
    ["port", '["203.0.113.4:443"]'],
    ["bracketed IPv6", '["[2001:db8::1]"]'],
    ["wildcard", '["203.0.113.*"]'],
    ["empty entry", '[""]'],
    ["padded entry", '[" 203.0.113.4"]'],
    ["number entry", "[3405803780]"],
    ["null entry", "[null]"],
    ["nested array", '[["203.0.113.4"]]'],
    ["one bad entry among good ones", '["203.0.113.4","not-an-ip"]'],
    ["hostname", '["example.com"]'],
  ])("invalidates the whole value for %s", (_name, raw) => {
    expect(parseTeamAllowlist(raw)).toEqual({ status: "invalid" });
  });

  it("rejects non-string input", () => {
    expect(parseTeamAllowlist(["203.0.113.4"])).toEqual({ status: "invalid" });
    expect(parseTeamAllowlist(42)).toEqual({ status: "invalid" });
  });

  it("accepts exactly 64 entries and rejects 65", () => {
    const entries = (count: number) =>
      JSON.stringify(Array.from({ length: count }, (_, index) => `203.0.113.${index + 1}`));
    expect(MAX_ALLOWLIST_ENTRIES).toBe(64);
    expect(parseTeamAllowlist(entries(64)).status).toBe("ok");
    expect(parseTeamAllowlist(entries(65))).toEqual({ status: "invalid" });
  });

  it("rejects an oversized secret before parsing it", () => {
    expect(parseTeamAllowlist(`["203.0.113.4"${" ".repeat(9000)}]`)).toEqual({ status: "invalid" });
  });
});

describe("isTeamMember", () => {
  const allowlist = ["203.0.113.4", "2001:db8::1"];

  it("matches canonical forms of listed addresses", () => {
    expect(isTeamMember("203.0.113.4", allowlist)).toBe(true);
    expect(isTeamMember("2001:DB8:0:0:0:0:0:1", allowlist)).toBe(true);
  });

  it("rejects unlisted, malformed and empty input", () => {
    expect(isTeamMember("198.51.100.9", allowlist)).toBe(false);
    expect(isTeamMember("203.0.113.4:80", allowlist)).toBe(false);
    expect(isTeamMember("", allowlist)).toBe(false);
    expect(isTeamMember("203.0.113.4", [])).toBe(false);
  });
});
