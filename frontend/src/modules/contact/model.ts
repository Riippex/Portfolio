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

export type ContactRelayStatus = "delivered" | "queued";

export interface ContactRelayResponse {
  status: ContactRelayStatus;
  outcome: ContactRelayStatus;
}

/**
 * The only documented success payloads of POST /v1/contact are
 * `{ "status": "delivered", "outcome": "delivered" }` and
 * `{ "status": "queued", "outcome": "queued" }`. Anything else, including an HTTP 200
 * that carries another status, a missing field, or a mismatched outcome, is not a
 * success and must never be shown as one.
 */
export function parseContactRelaySuccess(data: unknown): ContactRelayResponse | null {
  if (typeof data !== "object" || data === null || Array.isArray(data)) {
    return null;
  }

  const { status, outcome } = data as Record<string, unknown>;
  if ((status === "delivered" || status === "queued") && outcome === status) {
    return { status, outcome: status };
  }

  return null;
}

export interface ContactApiError {
  error: string;
  outcome?: string;
}

export type ContactSubmissionResult =
  | { ok: true; data: ContactRelayResponse }
  | { ok: false; error: string; status: number; outcome?: string };
