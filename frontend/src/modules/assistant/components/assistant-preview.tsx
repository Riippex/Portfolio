export function AssistantPreview() {
  return (
    <div className="assistant-card">
      <div className="assistant-bar">
        <span>Grounded session</span>
        <span>Preview · not connected</span>
      </div>
      <div className="terminal">
        <div className="terminal-line">
          <span className="label">You</span>
          <span>Why should I hire Rafael for an AI engineering role?</span>
        </div>
        <div className="terminal-line answer">
          <span className="label">R AI</span>
          <span>
            I’ll compare the role with <strong>documented projects and public evidence</strong>.
            Every material claim will include its source; undocumented experience will be marked as a gap.
          </span>
        </div>
      </div>
      <div className="assistant-actions" aria-label="Planned assistant prompts">
        <button disabled>Explain a project</button>
        <button disabled>Evaluate a vacancy</button>
        <button disabled>Show source evidence</button>
      </div>
    </div>
  );
}
