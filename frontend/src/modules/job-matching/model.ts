export interface JobMatchRequirement {
  requirementId: string;
  requirementText: string;
  category: string;
}

export interface JobEvidenceMatch {
  requirementId: string;
  requirementText: string;
  documentSlug: string;
  documentTitle: string;
  sectionHeading: string;
  claimId: string;
  citationUrl: string | null;
  evidenceStatus: string;
  groundingSummary: string;
}

export interface JobInferenceMatch {
  requirementId: string;
  requirementText: string;
  inferredCapability: string;
  supportingDocumentSlug: string;
  supportingTitle: string;
  supportingEvidenceStatus: string;
  rationale: string;
}

export interface JobGap {
  requirementId: string;
  requirementText: string;
  notice: string;
}

export interface JobAnalysisRequest {
  vacancyText: string;
}

export interface JobAnalysisResponse {
  roleSummary: string;
  extractedRequirements: JobMatchRequirement[];
  directMatches: JobEvidenceMatch[];
  inferences: JobInferenceMatch[];
  gaps: JobGap[];
  overallAssessment: string;
}
