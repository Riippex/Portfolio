// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const analyzeJob = vi.fn();

vi.mock("../api", () => ({
  analyzeJob: (...args: unknown[]) => analyzeJob(...args),
}));

import { JobMatcher } from "./job-matcher";

function directMatch(overrides: Record<string, unknown> = {}) {
  return {
    requirementId: "req-01",
    requirementText: "Autonomous Agents",
    documentSlug: "vextis",
    documentKind: "project",
    documentTitle: "Vextis",
    sectionHeading: "Architecture",
    sectionSlug: "architecture",
    claimId: "claim-vextis-01",
    citation: "docs/evidence/projects/vextis.md#architecture",
    citationUrl: null,
    evidenceStatus: "verified",
    groundingSummary: "Backed by verified evidence.",
    ...overrides,
  };
}

function inference(overrides: Record<string, unknown> = {}) {
  return {
    requirementId: "req-02",
    requirementText: "Autonomous Agents",
    inferredCapability: "Familiarity with autonomous agents",
    supportingDocumentSlug: "vextis",
    supportingDocumentKind: "project",
    supportingTitle: "Vextis",
    supportingSectionSlug: "architecture",
    supportingCitation: "docs/evidence/projects/vextis.md#architecture",
    supportingClaimId: "claim-vextis-01",
    supportingEvidenceStatus: "pending",
    rationale: "Related to Vextis.",
    ...overrides,
  };
}

function baseResponse(overrides: Record<string, unknown> = {}) {
  return {
    roleSummary: "Senior AI Engineer",
    extractedRequirements: [],
    directMatches: [],
    inferences: [],
    gaps: [],
    overallAssessment: "Assessment.",
    ...overrides,
  };
}

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
            documentKind: "project",
            documentTitle: "Vextis",
            sectionHeading: "Architecture",
            sectionSlug: "architecture",
            claimId: "claim-vextis-01",
            citation: "docs/evidence/projects/vextis.md#architecture",
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

  it("links project evidence to the case study and shows the real claim and citation", async () => {
    analyzeJob.mockResolvedValue({ ok: true, data: baseResponse({
      directMatches: [directMatch({ documentKind: "project", documentSlug: "vextis" })],
    }) });

    render(<JobMatcher />);
    fireEvent.change(screen.getByLabelText(/Paste Job Description/i), { target: { value: "Senior AI Engineer with Autonomous Agents" } });
    fireEvent.click(screen.getByRole("button", { name: /Evaluate alignment/i }));

    const link = await screen.findByRole("link", { name: /View case study/i });
    expect(link.getAttribute("href")).toBe("/projects/vextis");
    expect(screen.getByText(/claim-vextis-01/)).toBeTruthy();
    expect(screen.getByText(/docs\/evidence\/projects\/vextis\.md#architecture/)).toBeTruthy();
  });

  it("never links profile evidence to /projects/profile", async () => {
    analyzeJob.mockResolvedValue({ ok: true, data: baseResponse({
      directMatches: [directMatch({
        documentKind: "profile",
        documentSlug: "profile",
        documentTitle: "Rafael Patino",
        claimId: "claim-profile-01",
        citation: "docs/evidence/profile.md#focus-areas",
      })],
      inferences: [inference({ supportingDocumentKind: "profile", supportingDocumentSlug: "profile" })],
    }) });

    render(<JobMatcher />);
    fireEvent.change(screen.getByLabelText(/Paste Job Description/i), { target: { value: "Senior AI Engineer with Autonomous Agents" } });
    fireEvent.click(screen.getByRole("button", { name: /Evaluate alignment/i }));

    const links = await screen.findAllByRole("link", { name: /View profile/i });
    expect(links.length).toBe(2);
    for (const link of links) {
      expect(link.getAttribute("href")).toBe("/#top");
    }
    expect(document.querySelector('a[href="/projects/profile"]')).toBeNull();
  });

  it("renders no link for evidence kinds without an inspectable page", async () => {
    analyzeJob.mockResolvedValue({ ok: true, data: baseResponse({
      inferences: [inference({ supportingDocumentKind: "publication", supportingDocumentSlug: "paper-one" })],
    }) });

    render(<JobMatcher />);
    fireEvent.change(screen.getByLabelText(/Paste Job Description/i), { target: { value: "Senior AI Engineer with Autonomous Agents" } });
    fireEvent.click(screen.getByRole("button", { name: /Evaluate alignment/i }));

    expect(await screen.findByText(/Supporting evidence: Vextis/)).toBeTruthy();
    expect(screen.queryByRole("link", { name: /View/i })).toBeNull();
  });

  it("shows an unsupported qualifier as its own gap beside the inferred capability", async () => {
    analyzeJob.mockResolvedValue({ ok: true, data: baseResponse({
      inferences: [inference({ requirementId: "req-01" })],
      gaps: [{
        requirementId: "req-01",
        requirementText: "Five years of autonomous agents experience",
        notice: "The qualifier \"Five years\" is not documented.",
        unsupportedQualifier: "Five years",
      }],
    }) });

    render(<JobMatcher />);
    fireEvent.change(screen.getByLabelText(/Paste Job Description/i), { target: { value: "Senior AI Engineer with Autonomous Agents" } });
    fireEvent.click(screen.getByRole("button", { name: /Evaluate alignment/i }));

    expect(await screen.findByText("Not documented: Five years")).toBeTruthy();
    expect(screen.getByText(/qualifier "Five years" is not documented/)).toBeTruthy();
  });
});
