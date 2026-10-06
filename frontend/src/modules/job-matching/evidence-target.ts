export interface EvidenceTarget {
  readonly href: string;
  readonly label: string;
}

const SLUG_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;

/**
 * Resolves where an evidence document can be inspected. Only project records
 * have a detail page; the profile lives on the home page. Unknown kinds or
 * malformed slugs resolve to nothing so no link is invented.
 */
export function evidenceTarget(kind: string, slug: string): EvidenceTarget | null {
  if (!SLUG_PATTERN.test(slug)) {
    return null;
  }

  switch (kind) {
    case "project":
      return { href: `/projects/${slug}`, label: "View case study ↗" };
    case "profile":
      return { href: "/#top", label: "View profile ↗" };
    default:
      return null;
  }
}
