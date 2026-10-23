import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import Link from "next/link";
import { describe, expect, it } from "vitest";
import { BrandMark } from "./brand-mark";

const svgPaths = (markup: string) => [...markup.matchAll(/<path\b[^>]*\sd="([^"]+)"/g)].map((match) => match[1]);

describe("R Cut brand", () => {
  it("is decorative inside the named home link, with no redundant spoken letter", () => {
    const markup = renderToStaticMarkup(<Link href="/" aria-label="Rafael portfolio home"><BrandMark /></Link>);
    expect(markup).toContain('aria-label="Rafael portfolio home"');
    expect(markup).toContain('aria-hidden="true"');
    expect(markup).toContain('focusable="false"');
    expect(markup).toContain('viewBox="0 0 128 128"');
    expect(markup).toContain('fill="currentColor"');
    expect(markup).toContain('fill="#c7ff36"');
    expect(svgPaths(markup)).toHaveLength(2);
    expect(markup).not.toContain("<text");
  });

  it("uses the same glyph in the header and the light-on-dark SVG favicon", () => {
    const favicon = readFileSync(new URL("../../app/icon.svg", import.meta.url), "utf8");
    expect(svgPaths(favicon)).toEqual(svgPaths(renderToStaticMarkup(<BrandMark />)));
    expect(favicon).toContain('color="#f1efe8"');
    expect(favicon).toContain('fill="#11110f"');
    expect(favicon).toContain('fill="#c7ff36"');
    expect(favicon).not.toMatch(/<script|<image|https?:\/\/(?!www\.w3\.org)/);
  });

  it.each(["../../app/page.tsx", "../../app/projects/[slug]/page.tsx"])("reuses the mark in %s", (path) => {
    const source = readFileSync(new URL(path, import.meta.url), "utf8");
    expect(source).toContain("<BrandMark />");
    expect(source).not.toContain("RP<span>");
    expect(source).toContain('aria-label="Rafael portfolio home"');
  });

  it("provides 16, 32 and 48 pixel PNG frames in the ICO fallback", () => {
    const icon = readFileSync(new URL("../../app/favicon.ico", import.meta.url));
    expect(icon.readUInt16LE(0)).toBe(0);
    expect(icon.readUInt16LE(2)).toBe(1);
    expect(icon.readUInt16LE(4)).toBe(3);
    for (const [index, size] of [16, 32, 48].entries()) {
      const entry = 6 + index * 16;
      expect(icon[entry]).toBe(size);
      expect(icon[entry + 1]).toBe(size);
      const bytes = icon.readUInt32LE(entry + 8);
      const offset = icon.readUInt32LE(entry + 12);
      expect(offset).toBeGreaterThanOrEqual(54);
      expect(offset + bytes).toBeLessThanOrEqual(icon.length);
      expect(icon.subarray(offset, offset + 8).toString("hex")).toBe("89504e470d0a1a0a");
      expect(icon.readUInt32BE(offset + 16)).toBe(size);
      expect(icon.readUInt32BE(offset + 20)).toBe(size);
    }
  });
});
