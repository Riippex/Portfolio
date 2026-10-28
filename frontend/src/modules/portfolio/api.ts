import { buildServiceReadHeaders } from "@/modules/security/identity";
import type {
  Profile,
  ProfileLink,
  ProfileLinkPlatform,
  ProjectClaim,
  ProjectDetail,
  SelectedProject,
} from "./model";

export const MAX_PROFILE_LINKS = 2;
export const MAX_PROFILE_LINK_URL_LENGTH = 256;

const ALLOWED_LINKEDIN_HOSTS = new Set([
  "linkedin.com",
  "www.linkedin.com",
  "co.linkedin.com",
]);

const ALLOWED_GITHUB_HOSTS = new Set(["github.com", "www.github.com"]);

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

export function isProfileLinkPlatform(value: unknown): value is ProfileLinkPlatform {
  return value === "linkedin" || value === "github";
}

function isValidLinkedInPath(path: string): boolean {
  const prefix = "/in/";
  if (
    !path.startsWith(prefix) ||
    path.length <= prefix.length ||
    path.endsWith("/") ||
    path.includes("//") ||
    path.includes("..")
  ) {
    return false;
  }

  const handle = path.slice(prefix.length);
  if (handle.length < 1 || handle.length > 128) {
    return false;
  }

  for (let i = 0; i < handle.length; i += 1) {
    const ch = handle[i];
    if (ch === "%") {
      const hex = handle.slice(i + 1, i + 3);
      if (!/^[0-9A-Fa-f]{2}$/.test(hex)) {
        return false;
      }
      const code = Number.parseInt(hex, 16);
      if (
        code <= 0x20 ||
        code === 0x22 ||
        code === 0x27 ||
        code === 0x2f ||
        code === 0x3c ||
        code === 0x3e ||
        code === 0x5c ||
        code === 0x60 ||
        code === 0x7f
      ) {
        return false;
      }
      i += 2;
      continue;
    }

    if (!/^[A-Za-z0-9._-]$/.test(ch)) {
      return false;
    }
  }

  return true;
}

function isValidGitHubPath(path: string): boolean {
  if (!path.startsWith("/") || path.length <= 1 || path.indexOf("/", 1) !== -1) {
    return false;
  }

  const handle = path.slice(1);
  if (
    handle.length < 1 ||
    handle.length > 39 ||
    handle.startsWith("-") ||
    handle.endsWith("-") ||
    handle.includes("--")
  ) {
    return false;
  }

  return /^[A-Za-z0-9-]+$/.test(handle);
}

export function isValidProfileLinkUrl(
  platform: ProfileLinkPlatform,
  url: unknown
): url is string {
  if (
    !isProfileLinkPlatform(platform) ||
    typeof url !== "string" ||
    url.length === 0 ||
    url.length > MAX_PROFILE_LINK_URL_LENGTH
  ) {
    return false;
  }

  if (/[\u0000-\u0020\u007f-\u009f<>"'`\\?#@]/.test(url)) {
    return false;
  }

  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return false;
  }

  if (
    parsed.protocol !== "https:" ||
    parsed.username !== "" ||
    parsed.password !== "" ||
    parsed.port !== "" ||
    parsed.search !== "" ||
    parsed.hash !== ""
  ) {
    return false;
  }

  const host = parsed.hostname.toLowerCase();
  const expectedPrefix = `https://${host}/`;
  if (!url.toLowerCase().startsWith(expectedPrefix)) {
    return false;
  }

  const rawPath = url.slice(`https://${parsed.hostname}`.length);
  if (rawPath !== parsed.pathname) {
    return false;
  }

  if (platform === "linkedin") {
    return ALLOWED_LINKEDIN_HOSTS.has(host) && isValidLinkedInPath(rawPath);
  }

  if (platform === "github") {
    return ALLOWED_GITHUB_HOSTS.has(host) && isValidGitHubPath(rawPath);
  }

  return false;
}

export function isProfileLink(value: unknown): value is ProfileLink {
  if (!isRecord(value)) {
    return false;
  }
  const { platform, url } = value;
  return isProfileLinkPlatform(platform) && isValidProfileLinkUrl(platform, url);
}

export function isProfileLinkList(value: unknown): value is readonly ProfileLink[] {
  if (!Array.isArray(value) || value.length > MAX_PROFILE_LINKS) {
    return false;
  }

  const seenPlatforms = new Set<ProfileLinkPlatform>();
  for (const item of value) {
    if (!isProfileLink(item) || seenPlatforms.has(item.platform)) {
      return false;
    }
    seenPlatforms.add(item.platform);
  }

  return true;
}

export function sanitizeProfileLinks(links: unknown): readonly ProfileLink[] {
  if (!Array.isArray(links)) {
    return [];
  }

  const seenPlatforms = new Set<ProfileLinkPlatform>();
  const sanitized: ProfileLink[] = [];

  for (const item of links) {
    if (sanitized.length >= MAX_PROFILE_LINKS) {
      break;
    }
    if (!isProfileLink(item) || seenPlatforms.has(item.platform)) {
      continue;
    }
    seenPlatforms.add(item.platform);
    sanitized.push({ platform: item.platform, url: item.url });
  }

  return sanitized;
}

export function isProfile(value: unknown): value is Profile {
  if (!isRecord(value)) {
    return false;
  }
  const { name, headline, summary, evidenceStatus, focusAreas, links } = value;
  return (
    typeof name === "string" &&
    typeof headline === "string" &&
    typeof summary === "string" &&
    (evidenceStatus === "pending" || evidenceStatus === "verified") &&
    Array.isArray(focusAreas) &&
    focusAreas.every((item) => typeof item === "string") &&
    (links === undefined || isProfileLinkList(links))
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

    return {
      ok: true,
      data: {
        ...payload,
        links: sanitizeProfileLinks(payload.links),
      },
    };
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
