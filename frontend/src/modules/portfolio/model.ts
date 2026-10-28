export type EvidenceStatus = "pending" | "verified";

export type ProfileLinkPlatform = "linkedin" | "github";

export interface ProfileLink {
  readonly platform: ProfileLinkPlatform;
  readonly url: string;
}

export interface Profile {
  readonly name: string;
  readonly headline: string;
  readonly summary: string;
  readonly evidenceStatus: EvidenceStatus;
  readonly focusAreas: readonly string[];
  readonly links?: readonly ProfileLink[];
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
