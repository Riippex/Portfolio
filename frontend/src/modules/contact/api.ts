import type { ContactRelayRequest, ContactSubmissionResult } from "./model";

export async function sendContactMessage(
  request: ContactRelayRequest
): Promise<ContactSubmissionResult> {
  try {
    const res = await fetch("/api/contact", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Accept: "application/json",
      },
      body: JSON.stringify(request),
    });

    const data = await res.json().catch(() => null);

    if (res.ok && data?.status) {
      return {
        ok: true,
        data: {
          status: data.status,
          outcome: data.outcome ?? data.status,
        },
      };
    }

    const retryAfter = res.headers.get("retry-after");
    let errorMessage = data?.error ?? `Request failed with status ${res.status}`;
    if (res.status === 429 && retryAfter) {
      errorMessage = `Rate limit exceeded. Please wait ${retryAfter} seconds before trying again.`;
    }

    return {
      ok: false,
      error: errorMessage,
      status: res.status,
      outcome: data?.outcome,
    };
  } catch (err) {
    const error = err instanceof Error ? err.message : "Failed to connect to contact service.";
    return {
      ok: false,
      error,
      status: 0,
    };
  }
}
