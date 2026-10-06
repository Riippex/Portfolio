"use client";

import { useState } from "react";
import Link from "next/link";
import { analyzeJob } from "../api";
import { evidenceTarget } from "../evidence-target";
import type { JobAnalysisResponse } from "../model";

const SAMPLE_VACANCIES = [
  {
    label: "AI Systems Engineer",
    text: `Role: Senior AI Systems Engineer
Requirements:
- Architecture and design of Autonomous Agents and multi-agent coordination
- Edge-to-cloud computer vision pipelines and real-time processing
- Cloud-native services on GCP and Cloudflare Workers
- Strong backend experience with .NET / C# and modular monoliths
- Knowledge of COBOL or mainframe data pipelines`,
  },
  {
    label: "Full-Stack ML Engineer",
    text: `Position: Lead Full-Stack ML Engineer
Qualifications:
- Next.js and TypeScript frontend interfaces for AI workflows
- Deterministic RAG, evidence grounding, and safety boundaries
- Observability and strict privacy-preserving telemetry
- SAP ERP legacy enterprise integration`,
  },
];

export function JobMatcher() {
  const [vacancyText, setVacancyText] = useState("");
  const [isAnalyzing, setIsAnalyzing] = useState(false);
  const [result, setResult] = useState<JobAnalysisResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [activeTab, setActiveTab] = useState<"all" | "matches" | "inferences" | "gaps">("all");

  const charCount = vacancyText.trim().length;
  const isValidLength = charCount >= 10 && charCount <= 5000;

  async function handleAnalyze(e: React.FormEvent) {
    e.preventDefault();
    if (!isValidLength || isAnalyzing) return;

    setIsAnalyzing(true);
    setError(null);

    const res = await analyzeJob({ vacancyText });
    setIsAnalyzing(false);

    if (res.ok) {
      setResult(res.data);
    } else {
      setError(res.error);
    }
  }

  function handleClear() {
    setVacancyText("");
    setResult(null);
    setError(null);
  }

  function loadSample(text: string) {
    setVacancyText(text);
    setResult(null);
    setError(null);
  }

  return (
    <div className="job-matcher" aria-label="Job vacancy matching tool">
      <div className="matcher-input-card">
        <form onSubmit={handleAnalyze}>
          <div className="matcher-header">
            <label htmlFor="vacancy-input" className="matcher-label">
              Paste Job Description or Requirements
            </label>
            <div className="matcher-samples">
              <span className="samples-title">Try sample:</span>
              {SAMPLE_VACANCIES.map((sample) => (
                <button
                  type="button"
                  key={sample.label}
                  className="sample-pill"
                  onClick={() => loadSample(sample.text)}
                  disabled={isAnalyzing}
                >
                  {sample.label}
                </button>
              ))}
            </div>
          </div>

          <textarea
            id="vacancy-input"
            className="matcher-textarea"
            rows={7}
            placeholder="Paste role responsibilities, required skills, or tech stack requirements here (10 to 5,000 characters)..."
            value={vacancyText}
            onChange={(e) => setVacancyText(e.target.value)}
            disabled={isAnalyzing}
            aria-describedby="char-counter"
          />

          <div className="matcher-actions">
            <div id="char-counter" className={`char-counter ${charCount > 5000 ? "over-limit" : ""}`}>
              {charCount} / 5,000 characters {charCount > 0 && charCount < 10 && "(min 10)"}
            </div>

            <div className="button-group">
              {vacancyText.length > 0 && (
                <button
                  type="button"
                  className="button secondary button-small"
                  onClick={handleClear}
                  disabled={isAnalyzing}
                >
                  Clear
                </button>
              )}
              <button
                type="submit"
                className="button primary button-small"
                disabled={!isValidLength || isAnalyzing}
                aria-busy={isAnalyzing}
              >
                {isAnalyzing ? (
                  <>
                    <span className="spinner" aria-hidden="true" />
                    <span>Analyzing...</span>
                  </>
                ) : (
                  <span>Evaluate alignment ↘</span>
                )}
              </button>
            </div>
          </div>
        </form>

        {error && (
          <div className="status-banner error-banner" role="alert">
            <strong>Evaluation failed</strong>
            <p>{error}</p>
            <button type="button" className="retry-link" onClick={handleAnalyze}>
              Retry evaluation
            </button>
          </div>
        )}
      </div>

      {result && (
        <div className="matcher-results" aria-live="polite">
          <div className="assessment-card">
            <div className="assessment-eyebrow">
              <span>{result.roleSummary}</span>
              <span className="badge-grounded">Grounded Evaluation</span>
            </div>
            <p className="assessment-text">{result.overallAssessment}</p>

            <div className="metrics-row">
              <div className="metric-item">
                <span className="metric-val">{result.extractedRequirements.length}</span>
                <span className="metric-label">Requirements</span>
              </div>
              <div className="metric-item verified-metric">
                <span className="metric-val">{result.directMatches.length}</span>
                <span className="metric-label">Direct Matches</span>
              </div>
              <div className="metric-item inference-metric">
                <span className="metric-val">{result.inferences.length}</span>
                <span className="metric-label">Inferences</span>
              </div>
              <div className="metric-item gap-metric">
                <span className="metric-val">{result.gaps.length}</span>
                <span className="metric-label">Gaps</span>
              </div>
            </div>
          </div>

          <div className="filter-tabs" role="tablist" aria-label="Result category filters">
            <button
              type="button"
              role="tab"
              aria-selected={activeTab === "all"}
              className={`filter-tab ${activeTab === "all" ? "active" : ""}`}
              onClick={() => setActiveTab("all")}
            >
              All Breakdown ({result.extractedRequirements.length})
            </button>
            <button
              type="button"
              role="tab"
              aria-selected={activeTab === "matches"}
              className={`filter-tab ${activeTab === "matches" ? "active" : ""}`}
              onClick={() => setActiveTab("matches")}
            >
              Direct Evidence ({result.directMatches.length})
            </button>
            <button
              type="button"
              role="tab"
              aria-selected={activeTab === "inferences"}
              className={`filter-tab ${activeTab === "inferences" ? "active" : ""}`}
              onClick={() => setActiveTab("inferences")}
            >
              Supported Inferences ({result.inferences.length})
            </button>
            <button
              type="button"
              role="tab"
              aria-selected={activeTab === "gaps"}
              className={`filter-tab ${activeTab === "gaps" ? "active" : ""}`}
              onClick={() => setActiveTab("gaps")}
            >
              Gaps ({result.gaps.length})
            </button>
          </div>

          <div className="results-grid">
            {(activeTab === "all" || activeTab === "matches") && (
              <div className="result-category">
                <h3 className="category-title">
                  <span className="category-dot verified-dot" />
                  Direct Evidence Matches
                  <span className="category-count">{result.directMatches.length}</span>
                </h3>
                {result.directMatches.length > 0 ? (
                  result.directMatches.map((m) => {
                    const target = evidenceTarget(m.documentKind, m.documentSlug);
                    return (
                    <div className="evidence-card" key={m.requirementId}>
                      <div className="card-top">
                        <strong className="card-req">{m.requirementText}</strong>
                        <span className="status-pill verified">Evidence {m.evidenceStatus}</span>
                      </div>
                      <p className="card-summary">{m.groundingSummary}</p>
                      <div className="card-meta">
                        <span>
                          Source: {m.documentTitle} ({m.sectionHeading}) · Claim {m.claimId} · {m.citation}
                        </span>
                        {target && (
                          <Link href={target.href} className="card-link">
                            {target.label}
                          </Link>
                        )}
                      </div>
                    </div>
                    );
                  })
                ) : (
                  <p className="empty-category-note">
                    No direct verified claims match this requirement set.
                  </p>
                )}
              </div>
            )}

            {(activeTab === "all" || activeTab === "inferences") && (
              <div className="result-category">
                <h3 className="category-title">
                  <span className="category-dot inference-dot" />
                  Supported Inferences
                  <span className="category-count">{result.inferences.length}</span>
                </h3>
                {result.inferences.length > 0 ? (
                  result.inferences.map((inf) => {
                    const target = evidenceTarget(inf.supportingDocumentKind, inf.supportingDocumentSlug);
                    return (
                    <div className="inference-card" key={inf.requirementId}>
                      <div className="card-top">
                        <strong className="card-req">{inf.requirementText}</strong>
                        <span className="status-pill pending">Evidence {inf.supportingEvidenceStatus}</span>
                      </div>
                      <p className="card-summary">{inf.rationale}</p>
                      <div className="card-meta">
                        <span>
                          Supporting evidence: {inf.supportingTitle} · {inf.supportingCitation}
                        </span>
                        {target && (
                          <Link href={target.href} className="card-link">
                            {target.label}
                          </Link>
                        )}
                      </div>
                    </div>
                    );
                  })
                ) : (
                  <p className="empty-category-note">No supported inferences for this set.</p>
                )}
              </div>
            )}

            {(activeTab === "all" || activeTab === "gaps") && (
              <div className="result-category">
                <h3 className="category-title">
                  <span className="category-dot gap-dot" />
                  Identified Gaps
                  <span className="category-count">{result.gaps.length}</span>
                </h3>
                {result.gaps.length > 0 ? (
                  result.gaps.map((gap) => (
                    <div className="gap-card" key={`${gap.requirementId}-${gap.unsupportedQualifier ?? "requirement"}`}>
                      <div className="card-top">
                        <strong className="card-req">{gap.requirementText}</strong>
                        <span className="status-pill undocumented">
                          {gap.unsupportedQualifier ? `Not documented: ${gap.unsupportedQualifier}` : "Not Documented"}
                        </span>
                      </div>
                      <p className="card-summary">{gap.notice}</p>
                    </div>
                  ))
                ) : (
                  <p className="empty-category-note">No gaps identified.</p>
                )}
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
