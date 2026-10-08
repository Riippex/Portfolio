import { getBackendBaseUrl } from "@/modules/portfolio/api";
import { buildProxyIdentityHeaders } from "@/modules/security/identity";

export async function POST(request: Request): Promise<Response> {
  try {
    const body = await request.text();
    const backendUrl = `${getBackendBaseUrl()}/v1/jobs/analyze`;

    const { headers: identityHeaders, error: identityError } = await buildProxyIdentityHeaders(request, {
      method: "POST",
      path: "/v1/jobs/analyze",
    });
    if (identityError) {
      return new Response(JSON.stringify({ error: identityError.message }), {
        status: identityError.status,
        headers: { "Content-Type": "application/json" },
      });
    }

    const backendRes = await fetch(backendUrl, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Accept: "application/json",
        ...identityHeaders,
      },
      body,
    });

    const responseBody = await backendRes.text();
    const headers: Record<string, string> = {
      "Content-Type": "application/json",
    };

    const retryAfter = backendRes.headers.get("retry-after");
    if (retryAfter) {
      headers["Retry-After"] = retryAfter;
    }

    return new Response(responseBody, {
      status: backendRes.status,
      headers,
    });
  } catch (err) {
    const error = err instanceof Error ? err.message : "Failed to proxy job analysis to backend";
    return new Response(JSON.stringify({ error }), {
      status: 502,
      headers: { "Content-Type": "application/json" },
    });
  }
}
