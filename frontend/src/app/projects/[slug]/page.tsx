import { getProjectBySlug } from "@/modules/portfolio/api";
import type { Metadata } from "next";
import Link from "next/link";
import { notFound } from "next/navigation";

interface ProjectPageProps {
  params: Promise<{ slug: string }>;
}

export async function generateMetadata({ params }: ProjectPageProps): Promise<Metadata> {
  const { slug } = await params;
  const result = await getProjectBySlug(slug);
  if (!result.ok || !result.data) {
    return { title: "Project Case Study — Rafael Patiño" };
  }
  return {
    title: `${result.data.name} — Evidence Record — Rafael Patiño`,
    description: result.data.summary,
  };
}

export default async function ProjectPage({ params }: ProjectPageProps) {
  const { slug } = await params;
  const result = await getProjectBySlug(slug);

  if (!result.ok) {
    return (
      <main className="shell section" role="status" aria-live="polite">
        <nav className="detail-nav" aria-label="Breadcrumb navigation">
          <Link href="/" className="back-link">
            ← Back to selected systems
          </Link>
        </nav>
        <div className="status-banner">
          <strong>Project service unavailable</strong>
          <p>The project record could not be retrieved from the backend service.</p>
        </div>
      </main>
    );
  }

  if (!result.data) {
    notFound();
  }

  const project = result.data;

  return (
    <main>
      <nav className="shell nav" aria-label="Primary navigation">
        <Link className="wordmark" href="/" aria-label="Rafael portfolio home">
          RP<span>/</span>
        </Link>
        <div className="nav-links">
          <Link href="/#work">Work</Link>
          <Link href="/#assistant">R AI</Link>
          <Link href="/#assistant">Contact</Link>
        </div>
        <span className="status"><i /> Building in public</span>
      </nav>

      <article className="shell detail-hero">
        <nav className="detail-nav" aria-label="Breadcrumb navigation">
          <Link href="/#work" className="back-link">
            ← Back to selected systems
          </Link>
        </nav>

        <div className="eyebrow">
          Project Case Study · {project.slug}
        </div>

        <div className="detail-title-row">
          <h1>{project.name}</h1>
          <span className="project-state">
            Evidence {project.evidenceStatus}
          </span>
        </div>

        <p className="hero-copy">{project.summary}</p>

        <div className="detail-meta-grid" aria-label="Project metadata">
          <div className="meta-card">
            <span className="meta-label">Evidence status</span>
            <strong>{project.evidenceStatus}</strong>
          </div>
          <div className="meta-card">
            <span className="meta-label">Last reviewed</span>
            <strong>{project.lastReviewed}</strong>
          </div>
          <div className="meta-card">
            <span className="meta-label">Public source</span>
            {project.sourceUrl ? (
              <a
                href={project.sourceUrl}
                target="_blank"
                rel="noreferrer"
                className="source-link"
              >
                Inspect repository ↗
              </a>
            ) : (
              <span className="pending-badge">Pending owner verification</span>
            )}
          </div>
        </div>

        {project.evidenceStatus === "pending" && (
          <aside className="status-banner notice-banner" role="note">
            <strong>Evidence connection pending</strong>
            <p>
              Public case study artifacts and source repositories for this system
              are pending owner verification and public linking. All claims below
              remain marked pending until verified public evidence is connected.
            </p>
          </aside>
        )}
      </article>

      <section className="shell section claims-section">
        <div className="section-heading">
          <div>
            <span className="kicker">Claims &amp; Citations</span>
            <h2>Traceable statements.</h2>
          </div>
          <p>
            Every material claim is recorded with its identifier, verification status,
            and source citation.
          </p>
        </div>

        <div className="claims-table-wrapper" role="region" aria-label="Claims inventory">
          <table className="claims-table">
            <thead>
              <tr>
                <th scope="col">Claim ID</th>
                <th scope="col">Statement</th>
                <th scope="col">Status</th>
                <th scope="col">Citation</th>
              </tr>
            </thead>
            <tbody>
              {project.claims.map((claim) => (
                <tr key={claim.claimId}>
                  <td className="claim-id">
                    <code>{claim.claimId}</code>
                  </td>
                  <td className="claim-statement">{claim.statement}</td>
                  <td className="claim-status">
                    <span className={`status-pill ${claim.status}`}>
                      {claim.status}
                    </span>
                  </td>
                  <td className="claim-citation">
                    <small>{claim.citation}</small>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      <footer className="shell footer">
        <Link href="/#work" className="back-link">
          ← Return to selected systems
        </Link>
        <span>Next.js / .NET / Cloudflare / GCP</span>
      </footer>
    </main>
  );
}
