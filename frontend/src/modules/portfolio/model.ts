export type EvidenceStatus = "pending" | "verified";

export interface Profile {
  readonly name: string;
  readonly headline: string;
  readonly summary: string;
  readonly focusAreas: readonly string[];
}

export interface SelectedProject {
  readonly slug: string;
  readonly name: string;
  readonly summary: string;
  readonly evidenceStatus: EvidenceStatus;
}

export type ProjectSummary = SelectedProject;

export interface ProjectClaim {
  readonly claimId: string;
  readonly statement: string;
  readonly status: EvidenceStatus;
  readonly citation: string;
}

export interface ProjectDetail {
  readonly slug: string;
  readonly name: string;
  readonly summary: string;
  readonly evidenceStatus: EvidenceStatus;
  readonly sourceUrl: string | null;
  readonly lastReviewed: string;
  readonly claims: readonly ProjectClaim[];
}
