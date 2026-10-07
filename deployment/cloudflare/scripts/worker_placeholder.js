// Placeholder worker script for initial Terraform definition and validation.
// During release and deployment workflows, Vinext/Wrangler builds compile
// the frontend bundle from frontend/ into the Cloudflare Worker target.
export default {
  async fetch(request, env, ctx) {
    return new Response(
      "Rafael Portfolio Frontend Worker (Scaffold - pending deployment build)",
      {
        status: 200,
        headers: { "Content-Type": "text/plain" },
      }
    );
  },
};
