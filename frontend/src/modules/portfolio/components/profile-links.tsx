import { sanitizeProfileLinks } from "../api";
import type { ProfileLink, ProfileLinkPlatform } from "../model";

const PLATFORM_LABELS: Readonly<Record<ProfileLinkPlatform, string>> = {
  linkedin: "LinkedIn",
  github: "GitHub",
};

export interface ProfileLinksProps {
  readonly links?: readonly ProfileLink[] | null;
}

export function ProfileLinks({ links }: ProfileLinksProps) {
  const safeLinks = sanitizeProfileLinks(links);

  if (safeLinks.length === 0) {
    return null;
  }

  return (
    <ul className="profile-links" aria-label="Public profile links">
      {safeLinks.map((link) => {
        const label = PLATFORM_LABELS[link.platform];
        return (
          <li key={link.platform}>
            <a
              className="profile-link"
              href={link.url}
              target="_blank"
              rel="noopener noreferrer"
            >
              <span>{label}</span>
              <span aria-hidden="true">↗</span>
            </a>
          </li>
        );
      })}
    </ul>
  );
}
