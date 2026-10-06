import { describe, expect, it } from "vitest";
import { evidenceTarget } from "./evidence-target";

describe("evidenceTarget", () => {
  it("links project evidence to its case study", () => {
    expect(evidenceTarget("project", "vextis")).toEqual({
      href: "/projects/vextis",
      label: "View case study ↗",
    });
  });

  it("never links profile evidence to a project route", () => {
    const target = evidenceTarget("profile", "profile");
    expect(target).not.toBeNull();
    expect(target?.href).toBe("/#top");
    expect(target?.href).not.toContain("/projects/");
  });

  it("returns no link for unknown kinds or malformed slugs", () => {
    expect(evidenceTarget("publication", "paper-one")).toBeNull();
    expect(evidenceTarget("project", "../admin")).toBeNull();
    expect(evidenceTarget("project", "")).toBeNull();
  });
});
