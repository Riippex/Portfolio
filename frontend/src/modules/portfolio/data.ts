import type { SelectedProject } from "./model";

export const focusAreas = [
  "Autonomous agents",
  "Computer vision",
  "Cloud systems",
] as const;

export const selectedProjects = [
  {
    slug: "vextis",
    name: "Vextis",
    description: "Verified architecture and outcomes will be published here.",
    evidenceStatus: "pending",
  },
  {
    slug: "kinetiq-v",
    name: "Kinetiq V",
    description: "Verified architecture and outcomes will be published here.",
    evidenceStatus: "pending",
  },
  {
    slug: "jobty",
    name: "JobTY",
    description: "Verified architecture and outcomes will be published here.",
    evidenceStatus: "pending",
  },
] as const satisfies readonly SelectedProject[];
