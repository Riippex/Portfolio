import Link from "next/link";

export default function ProjectLoading() {
  return (
    <main className="shell section" role="status" aria-live="polite">
      <nav className="detail-nav" aria-label="Breadcrumb navigation">
        <Link href="/" className="back-link">
          ← Back to selected systems
        </Link>
      </nav>
      <div className="status-banner">
        <strong>Loading project evidence</strong>
        <p>Retrieving case study and claim records from backend service...</p>
      </div>
    </main>
  );
}
