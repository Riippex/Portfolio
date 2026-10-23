// Worker entry. Every request is admitted here, before Vinext serves static assets, cached
// responses, HTML, RSC payloads, optimised images or API routes. wrangler.jsonc sets
// assets.run_worker_first so Cloudflare does not answer asset requests ahead of this code,
// and compiled JS/CSS are fetched through the ASSETS binding only after admission.
import vinext from "vinext/server/fetch-handler";
import { admitRequest, type AdmissionEnv } from "../src/modules/security/admission";

const handler = vinext as unknown as {
  fetch(request: Request, env: unknown, ctx: unknown): Promise<Response>;
};

type WorkerEnv = AdmissionEnv & {
  readonly ASSETS?: { fetch(request: Request): Promise<Response> };
};

const worker = {
  async fetch(request: Request, env: unknown, ctx: unknown): Promise<Response> {
    const workerEnv = env as WorkerEnv;
    const decision = await admitRequest(request, workerEnv);
    if (decision.action === "respond") return decision.response;
    // run_worker_first disables Cloudflare's implicit asset serving. Vinext's app
    // handler treats compiled asset paths as missing, so dispatch these explicitly.
    // Never move this before admission or fall back to application HTML on a miss.
    if ((request.method === "GET" || request.method === "HEAD") && new URL(request.url).pathname.startsWith("/_next/static/")) {
      if (typeof workerEnv.ASSETS?.fetch !== "function") {
        return new Response("Static asset delivery is not configured.", {
          status: 503, headers: { "Cache-Control": "private, no-store", "Content-Type": "text/plain; charset=utf-8" },
        });
      }
      return workerEnv.ASSETS.fetch(decision.request);
    }
    return handler.fetch(decision.request, env, ctx);
  },
};

export default worker;
