import type { JobAnalysisRequest, JobAnalysisResponse } from "./model";

export type ApiResult<T> =
  | { readonly ok: true; readonly data: T }
  | { readonly ok: false; readonly error: string; readonly retryAfter?: number };

export async function analyzeJob(request: JobAnalysisRequest): Promise<ApiResult<JobAnalysisResponse>> {
  try {
    const res = await fetch("/api/jobs/analyze", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify(request),
    });

    if (!res.ok) {
      let error = `Request failed with HTTP ${res.status}`;
      try {
        const errJson = (await res.json()) as { error?: string };
        if (errJson?.error) {
          error = errJson.error;
        }
      } catch {
        // Fall back to HTTP status message
      }

      const retryAfterHeader = res.headers.get("retry-after");
      const retryAfter = retryAfterHeader ? parseInt(retryAfterHeader, 10) : undefined;

      return { ok: false, error, retryAfter };
    }

    const data = (await res.json()) as JobAnalysisResponse;
    return { ok: true, data };
  } catch (err) {
    const error = err instanceof Error ? err.message : "Failed to analyze job description";
    return { ok: false, error };
  }
}
