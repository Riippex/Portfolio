interface ProjectPresentation {
  readonly cover: string;
  readonly coverAlt: string;
  readonly repositories: readonly { readonly label: string; readonly url: string }[];
  readonly website?: { readonly url: string; readonly label: string; readonly note?: string };
}

// Presentation links can change independently of the pinned source used to verify claims.
const projects = new Map<string, ProjectPresentation>([
  ["vextis", {
    cover: "/projects/vextis.svg",
    coverAlt: "Illustration of a purchase order moving through inventory checks and human approval to invoicing.",
    repositories: [{ label: "GitHub", url: "https://github.com/Riippex/Vextis-ERP" }],
    website: { label: "Hosted demo", url: "https://vextis-erp.web.app/" },
  }],
  ["kinetiq-v", {
    cover: "/projects/kinetiq-v.svg",
    coverAlt: "Illustration of a selected person inside a camera frame, with tracked joints and a movement trace.",
    repositories: [
      { label: "GitHub · Product", url: "https://github.com/Riippex/kinetiq-v" },
      { label: "GitHub · Vision", url: "https://github.com/Riippex/kinetiq-v-vision" },
    ],
    website: {
      label: "Project site",
      url: "https://kinetiqv.rafaelpatinodiaz.com/",
      note: "Deployment may be offline.",
    },
  }],
  ["jobty", {
    cover: "/projects/jobty.svg",
    coverAlt: "Illustration of a résumé and vacancy feeding an assessment with reasons, concerns, and a recommendation.",
    repositories: [{ label: "GitHub", url: "https://github.com/Riippex/JobTY" }],
  }],
]);

export function getProjectPresentation(slug: string): ProjectPresentation | undefined {
  return projects.get(slug);
}
