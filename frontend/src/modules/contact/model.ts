export const MAX_NAME_LENGTH = 100;
export const MAX_EMAIL_LENGTH = 254;
export const MIN_MESSAGE_LENGTH = 10;
export const MAX_MESSAGE_LENGTH = 5000;
export const MAX_REQUEST_BODY_BYTES = 64 * 1024; // 64 KiB

export interface ContactRelayRequest {
  name: string;
  email: string;
  message: string;
  consent: boolean;
  turnstileToken?: string;
}

export interface ContactRelayResponse {
  status: "delivered" | "queued";
  outcome: string;
}

export interface ContactApiError {
  error: string;
  outcome?: string;
}

export type ContactSubmissionResult =
  | { ok: true; data: ContactRelayResponse }
  | { ok: false; error: string; status: number; outcome?: string };
