import { describe, expect, it } from "vitest";
import { normalizeCountry, readPlatformVisitor } from "./visitor";

const request = (headers: Record<string, string>, cf?: { country?: unknown }) => {
  const built = new Request("https://portfolio.example/api/jobs/analyze", { method: "POST", headers });
  if (cf) Object.defineProperty(built, "cf", { value: cf });
  return built;
};

describe("readPlatformVisitor", () => {
  it("reads the platform IP and country", () => {
    expect(readPlatformVisitor(request({ "CF-Connecting-IP": "203.0.113.9", "CF-IPCountry": "co" }))).toEqual({
      ip: "203.0.113.9",
      country: "CO",
    });
  });

  it("prefers the runtime country over the header", () => {
    const visitor = readPlatformVisitor(request({ "CF-Connecting-IP": "203.0.113.9", "CF-IPCountry": "US" }, { country: "CO" }));
    expect(visitor?.country).toBe("CO");
  });

  it("canonicalizes IPv6 and IPv4-mapped addresses", () => {
    expect(readPlatformVisitor(request({ "CF-Connecting-IP": "2001:DB8:0:0:0:0:0:1" }))?.ip).toBe("2001:db8::1");
    expect(readPlatformVisitor(request({ "CF-Connecting-IP": "::ffff:203.0.113.9" }))?.ip).toBe("203.0.113.9");
  });

  it("uses the unknown country when it is missing or malformed", () => {
    expect(readPlatformVisitor(request({ "CF-Connecting-IP": "203.0.113.9" }))?.country).toBe("XX");
    expect(readPlatformVisitor(request({ "CF-Connecting-IP": "203.0.113.9", "CF-IPCountry": "Colombia" }))?.country).toBe("XX");
    expect(normalizeCountry("T1")).toBe("T1");
  });

  it("returns null when the platform IP is missing, even if forwarding headers are present", () => {
    const forwarded = request({
      "X-Forwarded-For": "198.51.100.7",
      Forwarded: "for=198.51.100.7",
      "X-Real-IP": "198.51.100.7",
      "True-Client-IP": "198.51.100.7",
      "X-Client-IP": "198.51.100.7",
    });
    expect(readPlatformVisitor(forwarded)).toBeNull();
  });

  it.each(["", "unknown", "203.0.113.9, 198.51.100.7", "203.0.113.9:443", "203.0.113.0/24", "[2001:db8::1]"])(
    "returns null for an invalid platform IP %j instead of falling back",
    (value) => {
      const visitor = readPlatformVisitor(request({ "CF-Connecting-IP": value, "X-Forwarded-For": "198.51.100.7" }));
      expect(visitor).toBeNull();
    }
  );
});
