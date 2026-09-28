import { AssistantPreview } from "@/modules/assistant/components/assistant-preview";
import { focusAreas, selectedProjects } from "@/modules/portfolio/data";

export default function Home() {
  return (
    <main>
      <nav className="shell nav" aria-label="Primary navigation">
        <a className="wordmark" href="#top" aria-label="Rafael portfolio home">
          RP<span>/</span>
        </a>
        <div className="nav-links">
          <a href="#work">Work</a>
          <a href="#assistant">Rafael AI</a>
          <a href="#assistant">Contact</a>
        </div>
        <span className="status"><i /> Building in public</span>
      </nav>

      <section id="top" className="shell hero">
        <div className="eyebrow">AI systems engineer · Colombia</div>
        <h1>I build AI systems<span>that can explain their work.</span></h1>
        <p className="hero-copy">
          Autonomous agents, real-time computer vision, and cloud systems—built
          with evidence, observability, and a healthy distrust of magic.
        </p>
        <div className="hero-actions">
          <a className="button primary" href="#work">Explore selected work <span>↘</span></a>
          <a className="button secondary" href="#assistant">Ask Rafael AI <span>⌁</span></a>
        </div>
        <div className="focus-grid" aria-label="Focus areas">
          {focusAreas.map((area, index) => (
            <div className="focus-item" key={area}>
              <span>0{index + 1}</span>
              <strong>{area}</strong>
            </div>
          ))}
        </div>
      </section>

      <section id="work" className="shell section">
        <div className="section-heading">
          <div><span className="kicker">Selected systems</span><h2>Proof over promises.</h2></div>
          <p>Case studies will be generated only from verified repository and résumé evidence.</p>
        </div>
        <div className="project-grid">
          {selectedProjects.map((project, index) => (
            <article className="project-card" key={project.slug}>
              <div className="project-number">0{index + 1}</div>
              <div>
                <span className="project-state">Evidence {project.evidenceStatus}</span>
                <h3>{project.name}</h3>
                <p>{project.description}</p>
              </div>
              <span className="project-arrow" aria-hidden="true">↗</span>
            </article>
          ))}
        </div>
      </section>

      <section id="assistant" className="shell section assistant-section">
        <div className="section-heading">
          <div><span className="kicker">The interactive layer</span><h2>Meet Rafael AI.</h2></div>
          <p>A grounded portfolio agent designed to cite its claims and admit when evidence is missing.</p>
        </div>
        <AssistantPreview />
      </section>

      <footer className="shell footer">
        <span>Rafael Patiño · AI systems engineer</span>
        <span>Next.js / .NET / Cloudflare / GCP</span>
      </footer>
    </main>
  );
}
