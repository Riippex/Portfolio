export type EvidenceStatus = "pending" | "verified";

export interface SelectedProject {
  slug: string;
  name: string;
  description: string;
  evidenceStatus: EvidenceStatus;
}
