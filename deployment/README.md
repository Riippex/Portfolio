# Deployment

Infrastructure lives here once it is reproducible.

- `cloudflare/`: frontend Worker, DNS, Turnstile, and edge configuration.
- `gcp/`: Cloud Run, Firestore, Storage, Vertex AI, and secrets.

No production resources are declared in the initial scaffold.
