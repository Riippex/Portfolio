// @vitest-environment jsdom
import { cleanup, render, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import Home from "@/app/page";
import type { ProfileLink } from "../model";
import { ProfileLinks } from "./profile-links";

const LINKEDIN_URL = "https://co.linkedin.com/in/rafael-pati%C3%B1o-diaz";
const GITHUB_URL = "https://github.com/Riippex";

describe("ProfileLinks and Home profile links rendering", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllEnvs();
    vi.unstubAllGlobals();
  });

  it("renders clearly named, keyboard-accessible LinkedIn and GitHub links with safe rel", () => {
    render(
      <ProfileLinks
        links={[
          { platform: "linkedin", url: LINKEDIN_URL },
          { platform: "github", url: GITHUB_URL },
        ]}
      />
    );

    const list = screen.getByRole("list", { name: "Public profile links" });
    const linkedin = within(list).getByRole("link", { name: "LinkedIn" });
    const github = within(list).getByRole("link", { name: "GitHub" });

    expect(linkedin.getAttribute("href")).toBe(LINKEDIN_URL);
    expect(linkedin.getAttribute("target")).toBe("_blank");
    expect(linkedin.getAttribute("rel")).toBe("noopener noreferrer");

    expect(github.getAttribute("href")).toBe(GITHUB_URL);
    expect(github.getAttribute("target")).toBe("_blank");
    expect(github.getAttribute("rel")).toBe("noopener noreferrer");
  });

  it("renders nothing when links are missing or empty", () => {
    const { container: missingContainer } = render(<ProfileLinks />);
    expect(missingContainer.firstChild).toBeNull();

    const { container: emptyContainer } = render(<ProfileLinks links={[]} />);
    expect(emptyContainer.firstChild).toBeNull();
  });

  it("refuses to render malformed, deceptive, duplicate, or script/markup destinations", () => {
    const untrustedLinks = [
      { platform: "github", url: "javascript:alert(1)" },
      { platform: "github", url: "data:text/html,<script>alert(1)</script>" },
      { platform: "github", url: "https://github.com@evil.example/Riippex" },
      { platform: "linkedin", url: "https://linkedin.com.evil.example/in/rafael" },
      { platform: "github", url: 'https://github.com/Riippex"><img src=x onerror=alert(1)>' },
      { platform: "twitter", url: "https://twitter.com/example" },
    ] as unknown as readonly ProfileLink[];

    const { container } = render(<ProfileLinks links={untrustedLinks} />);
    expect(container.firstChild).toBeNull();
    expect(screen.queryByRole("link")).toBeNull();
  });

  it("renders public links on the real Home page and removes them when absent from canonical profile data", async () => {
    vi.stubEnv("PORTFOLIO_STAGE", "prod");
    vi.stubEnv("PORTFOLIO_BACKEND_URL", "https://backend.example");

    vi.stubGlobal(
      "fetch",
      vi.fn(async (url: string) => {
        if (String(url).endsWith("/v1/profile")) {
          return new Response(
            JSON.stringify({
              name: "Rafael Patiño",
              headline: "Software & AI Engineer",
              summary: "Verified profile summary.",
              evidenceStatus: "verified",
              focusAreas: ["Software engineering", "AI engineering"],
              links: [
                { platform: "linkedin", url: LINKEDIN_URL },
                { platform: "github", url: GITHUB_URL },
              ],
            }),
            { status: 200 }
          );
        }
        return new Response(
          JSON.stringify([
            {
              slug: "vextis",
              name: "Vextis",
              summary: "Verified architecture and outcomes will be published here.",
              evidenceStatus: "pending",
            },
          ]),
          { status: 200 }
        );
      })
    );

    render(await Home());

    expect(screen.getByRole("link", { name: "Rafael portfolio home" }).getAttribute("href")).toBe("#top");
    expect(screen.getByRole("link", { name: "LinkedIn" }).getAttribute("href")).toBe(LINKEDIN_URL);
    expect(screen.getByRole("link", { name: "GitHub" }).getAttribute("href")).toBe(GITHUB_URL);

    cleanup();

    // Simulate removing GitHub from canonical evidence so only LinkedIn remains
    vi.stubGlobal(
      "fetch",
      vi.fn(async (url: string) => {
        if (String(url).endsWith("/v1/profile")) {
          return new Response(
            JSON.stringify({
              name: "Rafael Patiño",
              headline: "Software & AI Engineer",
              summary: "Verified profile summary.",
              evidenceStatus: "verified",
              focusAreas: ["Software engineering"],
              links: [{ platform: "linkedin", url: LINKEDIN_URL }],
            }),
            { status: 200 }
          );
        }
        return new Response(JSON.stringify([]), { status: 200 });
      })
    );

    render(await Home());

    expect(screen.getByRole("link", { name: "LinkedIn" }).getAttribute("href")).toBe(LINKEDIN_URL);
    expect(screen.queryByRole("link", { name: "GitHub" })).toBeNull();
  });
});
