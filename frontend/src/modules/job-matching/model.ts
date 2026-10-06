export interface JobMatchRequirement {
  requirementId: string;
  requirementText: string;
  category: string;
}

export interface JobEvidenceMatch {
  requirementId: string;
  requirementText: string;
  documentSlug: string;
  documentKind: string;
  documentTitle: string;
  sectionHeading: string;
  sectionSlug: string;
  claimId: string;
  citation: string;
  citationUrl: string | null;
  evidenceStatus: string;
  groundingSummary: string;
}

export interface JobInferenceMatch {
  requirementId: string;
  requirementText: string;
  inferredCapability: string;
  supportingDocumentSlug: string;
  supportingDocumentKind: string;
  supportingTitle: string;
  supportingSectionSlug: string;
  supportingCitation: string;
  supportingClaimId: string | null;
  supportingEvidenceStatus: string;
  rationale: string;
}

export interface JobGap {
  requirementId: string;
  requirementText: string;
  notice: string;
  unsupportedQualifier?: string | null;
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
