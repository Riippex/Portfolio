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
