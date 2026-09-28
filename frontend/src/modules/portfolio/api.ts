import type { Profile, SelectedProject } from "./model";

export type ApiResult<T> =
  | { readonly ok: true; readonly data: T }
  | { readonly ok: false; readonly error: string };

export function getBackendBaseUrl(): string {
  const envUrl = process.env.PORTFOLIO_BACKEND_URL || process.env.BACKEND_API_URL;
  const baseUrl = envUrl && envUrl.trim().length > 0 ? envUrl.trim() : "http://localhost:5233";
  return baseUrl.replace(/\/+$/, "");
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

export function isProfile(value: unknown): value is Profile {
  if (!isRecord(value)) {
    return false;
  }
  const { name, headline, summary, focusAreas } = value;
  return (
    typeof name === "string" &&
    typeof headline === "string" &&
    typeof summary === "string" &&
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

export async function getProfile(): Promise<ApiResult<Profile>> {
  const url = `${getBackendBaseUrl()}/v1/profile`;
  try {
    const res = await fetch(url, {
      cache: "no-store",
      headers: {
        Accept: "application/json",
      },
    });

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
  const url = `${getBackendBaseUrl()}/v1/projects`;
  try {
    const res = await fetch(url, {
      cache: "no-store",
      headers: {
        Accept: "application/json",
      },
    });

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
