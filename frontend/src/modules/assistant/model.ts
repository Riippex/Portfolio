export interface AssistantCitation {
  readonly chunkId: string;
  readonly documentId: string;
  readonly slug: string;
  readonly title: string;
  readonly sectionHeading: string;
  readonly sourceUrl: string | null;
  readonly evidenceStatus: "pending" | "verified";
  readonly version: string;
  readonly claims: readonly string[];
}

export type AssistantGroundingStatus = "grounded" | "not_documented";

export interface AssistantChatMessage {
  readonly id: string;
  readonly role: "user" | "assistant";
  readonly content: string;
  readonly citations?: readonly AssistantCitation[];
  readonly groundingStatus?: AssistantGroundingStatus;
  readonly isStreaming?: boolean;
}

export interface AssistantChatRequest {
  readonly message: string;
  readonly slug?: string;
  readonly turnstileToken?: string;
}

export type AssistantStreamEvent =
  | { readonly type: "status"; readonly groundingStatus: AssistantGroundingStatus }
  | { readonly type: "token"; readonly text: string }
  | { readonly type: "citation"; readonly citation: AssistantCitation }
  | { readonly type: "error"; readonly error: string }
  | { readonly type: "done"; readonly done: boolean };
