// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const analyzeJob = vi.fn();

vi.mock("../api", () => ({
  analyzeJob: (...args: unknown[]) => analyzeJob(...args),
}));

import { JobMatcher } from "./job-matcher";

describe("JobMatcher component", () => {
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it("disables the evaluate button when input is less than 10 characters", () => {
    render(<JobMatcher />);
    const textarea = screen.getByLabelText(/Paste Job Description/i);
    const submitBtn = screen.getByRole("button", { name: /Evaluate alignment/i });

    expect(submitBtn.hasAttribute("disabled")).toBe(true);

    fireEvent.change(textarea, { target: { value: "Short" } });
    expect(submitBtn.hasAttribute("disabled")).toBe(true);

    fireEvent.change(textarea, { target: { value: "A vacancy with sufficient characters" } });
    expect(submitBtn.hasAttribute("disabled")).toBe(false);
  });

  it("submits the vacancy and renders direct matches, inferences, gaps and assessment", async () => {
    analyzeJob.mockResolvedValue({
      ok: true,
      data: {
        roleSummary: "Senior AI Engineer",
        extractedRequirements: [
          { requirementId: "req-01", requirementText: "Autonomous Agents", category: "AI" },
          { requirementId: "req-02", requirementText: "COBOL", category: "Legacy" },
        ],
        directMatches: [
          {
            requirementId: "req-01",
            requirementText: "Autonomous Agents",
            documentSlug: "vextis",
            documentTitle: "Vextis",
            sectionHeading: "Architecture",
            claimId: "claim-vextis-01",
            citationUrl: "https://github.com/example/vextis",
            evidenceStatus: "verified",
            groundingSummary: "Backed by verified evidence in Vextis.",
          },
        ],
        inferences: [],
        gaps: [
          {
            requirementId: "req-02",
            requirementText: "COBOL",
            notice: "No documented evidence found in Rafael's public portfolio.",
          },
        ],
        overallAssessment: "Omitted artificial scores. 1 direct match and 1 gap.",
      },
    });

    render(<JobMatcher />);
    const textarea = screen.getByLabelText(/Paste Job Description/i);
    fireEvent.change(textarea, { target: { value: "Senior AI Engineer with Autonomous Agents and COBOL" } });

    const submitBtn = screen.getByRole("button", { name: /Evaluate alignment/i });
    fireEvent.click(submitBtn);

    expect(await screen.findByText("Senior AI Engineer")).toBeTruthy();
    expect(screen.getByText("Omitted artificial scores. 1 direct match and 1 gap.")).toBeTruthy();
    expect(screen.getByText("Backed by verified evidence in Vextis.")).toBeTruthy();
    expect(screen.getByText("No documented evidence found in Rafael's public portfolio.")).toBeTruthy();
  });

  it("renders error banner when analysis fails", async () => {
    analyzeJob.mockResolvedValue({
      ok: false,
      error: "Rate limit exceeded. Please wait 60 seconds.",
    });

    render(<JobMatcher />);
    const textarea = screen.getByLabelText(/Paste Job Description/i);
    fireEvent.change(textarea, { target: { value: "Senior AI Engineer with Autonomous Agents" } });

    const submitBtn = screen.getByRole("button", { name: /Evaluate alignment/i });
    fireEvent.click(submitBtn);

    const banner = await screen.findByRole("alert");
    expect(banner.textContent).toContain("Rate limit exceeded");
  });

  it("loads sample vacancy when sample button is clicked", () => {
    render(<JobMatcher />);
    const sampleBtn = screen.getByRole("button", { name: "AI Systems Engineer" });
    fireEvent.click(sampleBtn);

    const textarea = screen.getByLabelText(/Paste Job Description/i) as HTMLTextAreaElement;
    expect(textarea.value).toContain("Autonomous Agents");
  });
});
