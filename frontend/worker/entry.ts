// Worker entry. Every request is admitted here, before Vinext serves static assets, cached
// responses, HTML, RSC payloads, optimised images or API routes. wrangler.jsonc sets
// assets.run_worker_first so Cloudflare does not answer asset requests ahead of this code,
// and the Vinext handler fetches assets through the ASSETS binding only after admission.
import vinext from "vinext/server/fetch-handler";
import { admitRequest, type AdmissionEnv } from "../src/modules/security/admission";

const handler = vinext as unknown as {
  fetch(request: Request, env: unknown, ctx: unknown): Promise<Response>;
};

const worker = {
  async fetch(request: Request, env: unknown, ctx: unknown): Promise<Response> {
    const decision = await admitRequest(request, env as AdmissionEnv);
    if (decision.action === "respond") return decision.response;
    return handler.fetch(decision.request, env, ctx);
  },
};

export default worker;
