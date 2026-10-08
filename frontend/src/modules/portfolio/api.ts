import { buildServiceReadHeaders } from "@/modules/security/identity";
import type { Profile, ProjectClaim, ProjectDetail, SelectedProject } from "./model";

export type ApiResult<T> =
  | { readonly ok: true; readonly data: T }
  | { readonly ok: false; readonly error: string };

export function getBackendBaseUrl(): string {
  const envUrl = process.env.PORTFOLIO_BACKEND_URL || process.env.BACKEND_API_URL;
  const baseUrl = envUrl && envUrl.trim().length > 0 ? envUrl.trim() : "http://localhost:5233";
  return baseUrl.replace(/\/+$/, "");
}

// Reads public data from the backend. The private dev stage requires a Worker-signed
// service-read identity bound to the exact path; other stages send no identity.
// The signed path is the decoded request path; urlPath is its encoded form on the wire.
async function fetchBackendJson(path: string, urlPath: string = path): Promise<Response> {
  const { headers: identityHeaders, error } = await buildServiceReadHeaders(path);
  if (error) {
    throw new Error(error.message);
  }

  return fetch(`${getBackendBaseUrl()}${urlPath}`, {
    cache: "no-store",
    headers: {
      Accept: "application/json",
      ...identityHeaders,
    },
  });
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

export function isProfile(value: unknown): value is Profile {
  if (!isRecord(value)) {
    return false;
  }
  const { name, headline, summary, evidenceStatus, focusAreas } = value;
  return (
    typeof name === "string" &&
    typeof headline === "string" &&
    typeof summary === "string" &&
    (evidenceStatus === "pending" || evidenceStatus === "verified") &&
    Array.isArray(focusAreas) &&
    focusAreas.every((item) => typeof item === "string")
  );
}

export function isSelectedProject(value: unknown): value is SelectedProject {
  if (!isRecord(value)) {
    return false;
  }
  const { slug, name, summary, evidenceStatus } = value;
  return (
    typeof slug === "string" &&
    typeof name === "string" &&
    typeof summary === "string" &&
    (evidenceStatus === "pending" || evidenceStatus === "verified")
  );
}

export function isSelectedProjectList(value: unknown): value is SelectedProject[] {
  return Array.isArray(value) && value.every(isSelectedProject);
}

export function isProjectClaim(value: unknown): value is ProjectClaim {
  if (!isRecord(value)) {
    return false;
  }
  const { claimId, statement, status, citation } = value;
  return (
    typeof claimId === "string" &&
    typeof statement === "string" &&
    (status === "pending" || status === "verified") &&
    typeof citation === "string"
  );
}

export function isProjectDetail(value: unknown): value is ProjectDetail {
  if (!isRecord(value)) {
    return false;
  }
  const { slug, name, summary, evidenceStatus, sourceUrl, lastReviewed, claims } = value;
  return (
    typeof slug === "string" &&
    typeof name === "string" &&
    typeof summary === "string" &&
    (evidenceStatus === "pending" || evidenceStatus === "verified") &&
    (sourceUrl === null || typeof sourceUrl === "string") &&
    typeof lastReviewed === "string" &&
    Array.isArray(claims) &&
    claims.every(isProjectClaim)
  );
}

export async function getProfile(): Promise<ApiResult<Profile>> {
  try {
    const res = await fetchBackendJson("/v1/profile");

    if (!res.ok) {
      return { ok: false, error: `Backend responded with HTTP ${res.status}` };
    }

    const payload: unknown = await res.json();
    if (!isProfile(payload)) {
      return { ok: false, error: "Backend response did not match Profile schema" };
    }

    return { ok: true, data: payload };
  } catch (err) {
    const error = err instanceof Error ? err.message : "Failed to connect to backend profile service";
    return { ok: false, error };
  }
}

export async function getSelectedProjects(): Promise<ApiResult<readonly SelectedProject[]>> {
  try {
    const res = await fetchBackendJson("/v1/projects");

    if (!res.ok) {
      return { ok: false, error: `Backend responded with HTTP ${res.status}` };
    }

    const payload: unknown = await res.json();
    if (!isSelectedProjectList(payload)) {
      return { ok: false, error: "Backend response did not match SelectedProject list schema" };
    }

    return { ok: true, data: payload };
  } catch (err) {
    const error = err instanceof Error ? err.message : "Failed to connect to backend projects service";
    return { ok: false, error };
  }
}

export async function getProjectBySlug(slug: string): Promise<ApiResult<ProjectDetail | null>> {
  try {
    const res = await fetchBackendJson(`/v1/projects/${slug}`, `/v1/projects/${encodeURIComponent(slug)}`);

    if (res.status === 404) {
      return { ok: true, data: null };
    }

    if (!res.ok) {
      return { ok: false, error: `Backend responded with HTTP ${res.status}` };
    }

    const payload: unknown = await res.json();
    if (!isProjectDetail(payload)) {
      return { ok: false, error: "Backend response did not match ProjectDetail schema" };
    }

    return { ok: true, data: payload };
  } catch (err) {
    const error = err instanceof Error ? err.message : "Failed to connect to backend projects service";
    return { ok: false, error };
  }
}
