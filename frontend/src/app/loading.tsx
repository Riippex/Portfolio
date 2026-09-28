export default function Loading() {
  return (
    <main className="shell loading-shell" role="status" aria-live="polite">
      <div className="status-banner">
        <strong>Loading portfolio</strong>
        <p>Connecting to backend services...</p>
      </div>
    </main>
  );
}
