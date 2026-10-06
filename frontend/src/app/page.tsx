import { AssistantChat } from "@/modules/assistant/components/assistant-chat";
import { JobMatcher } from "@/modules/job-matching/components/job-matcher";
import { getProfile, getSelectedProjects } from "@/modules/portfolio/api";
import Link from "next/link";

export default async function Home() {
  const [profileResult, projectsResult] = await Promise.all([
    getProfile(),
    getSelectedProjects(),
  ]);

  const profile = profileResult.ok ? profileResult.data : null;
  const projects = projectsResult.ok ? projectsResult.data : null;

  return (
    <main>
      <nav className="shell nav" aria-label="Primary navigation">
        <a className="wordmark" href="#top" aria-label="Rafael portfolio home">
          RP<span>/</span>
        </a>
        <div className="nav-links">
          <a href="#work">Work</a>
          <a href="#assistant">Rafael AI</a>
          <a href="#match">Job Match</a>
          <a href="#assistant">Contact</a>
        </div>
        <span className="status"><i /> Building in public</span>
      </nav>

      <section id="top" className="shell hero">
        <div className="eyebrow">
          {profile ? `${profile.headline} · Colombia` : "Profile unavailable · Colombia"}
        </div>
        {profile && (
          <span className={`status-pill ${profile.evidenceStatus} hero-evidence`}>
            Profile evidence {profile.evidenceStatus}
          </span>
        )}
        <h1>I build AI systems<span>that can explain their work.</span></h1>
        {profile ? (
          <p className="hero-copy">{profile.summary}</p>
        ) : (
          <p className="hero-copy" role="status">
            Profile summary is temporarily unavailable from the backend service.
          </p>
        )}
        <div className="hero-actions">
          <a className="button primary" href="#work">Explore selected work <span>↘</span></a>
          <a className="button secondary" href="#assistant">Ask Rafael AI <span>⌁</span></a>
        </div>
        <div className="focus-grid" aria-label="Focus areas">
          {profile && profile.focusAreas.length > 0 ? (
            profile.focusAreas.map((area, index) => (
              <div className="focus-item" key={area}>
                <span>0{index + 1}</span>
                <strong>{area}</strong>
              </div>
            ))
          ) : (
            <div className="focus-unavailable" role="status" aria-live="polite">
              Focus areas are temporarily unavailable from the backend service.
            </div>
          )}
        </div>
      </section>

      <section id="work" className="shell section">
        <div className="section-heading">
          <div><span className="kicker">Selected systems</span><h2>Proof over promises.</h2></div>
          <p>Case studies will be generated only from verified repository and résumé evidence.</p>
        </div>
        <div className="project-grid">
          {projects && projects.length > 0 ? (
            projects.map((project, index) => (
              <Link
                href={`/projects/${project.slug}`}
                className="project-card"
                key={project.slug}
                aria-label={`View evidence-backed case study for ${project.name}`}
              >
                <div className="project-number">0{index + 1}</div>
                <div>
                  <span className="project-state">Evidence {project.evidenceStatus}</span>
                  <h3>{project.name}</h3>
                  <p>{project.summary}</p>
                </div>
                <span className="project-arrow" aria-hidden="true">↗</span>
              </Link>
            ))
          ) : (
            <div className="status-banner" role="status" aria-live="polite">
              <strong>Projects catalog unavailable</strong>
              <p>Selected project records could not be retrieved from the backend service.</p>
            </div>
          )}
        </div>
      </section>

      <section id="assistant" className="shell section assistant-section">
        <div className="section-heading">
          <div><span className="kicker">The interactive layer</span><h2>Meet Rafael AI.</h2></div>
          <p>A grounded portfolio agent designed to cite its claims and admit when evidence is missing.</p>
        </div>
        <AssistantChat />
      </section>

      <section id="match" className="shell section match-section">
        <div className="section-heading">
          <div><span className="kicker">Role alignment</span><h2>Job Match & Gap Analysis.</h2></div>
          <p>Evaluate vacancies against verified evidence. Unverified claims remain inferences or gaps; no arbitrary scores.</p>
        </div>
        <JobMatcher />
      </section>

      <footer className="shell footer">
        <span>{profile ? `${profile.name} · ${profile.headline}` : "Rafael Patiño · Profile unavailable"}</span>
        <span>Next.js / .NET / Cloudflare / GCP</span>
      </footer>
    </main>
  );
}
