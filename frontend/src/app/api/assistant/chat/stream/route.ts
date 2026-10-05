import { getBackendBaseUrl } from "@/modules/portfolio/api";

async function hmacSha256Hex(secret: string, message: string): Promise<string> {
  const encoder = new TextEncoder();
  const key = await crypto.subtle.importKey(
    "raw",
    encoder.encode(secret),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"]
  );
  const signature = await crypto.subtle.sign("HMAC", key, encoder.encode(message));
  return Array.from(new Uint8Array(signature), (byte) => byte.toString(16).padStart(2, "0")).join("");
}

export async function POST(request: Request): Promise<Response> {
  try {
    const body = await request.text();
    const backendUrl = `${getBackendBaseUrl()}/v1/assistant/chat/stream`;

    // Caller-controlled identity headers are never forwarded verbatim. The
    // visitor id asserted by the edge (CF-Connecting-IP) is only propagated as
    // an HMAC-signed identity the backend can verify.
    const identityHeaders: Record<string, string> = {};
    const identitySecret = process.env.ASSISTANT_PROXY_IDENTITY_SECRET;

    if (!identitySecret && process.env.NODE_ENV === "production") {
      return new Response(
        JSON.stringify({ error: "Assistant identity boundary is not configured." }),
        { status: 503, headers: { "Content-Type": "application/json" } }
      );
    }

    const visitorId = request.headers.get("CF-Connecting-IP")?.trim();
    if (identitySecret && visitorId) {
      identityHeaders["X-Client-Key"] = visitorId;
      identityHeaders["X-Client-Key-Proof"] = await hmacSha256Hex(identitySecret, visitorId);
    }

    const backendRes = await fetch(backendUrl, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Accept: "text/event-stream",
        ...identityHeaders,
      },
      body,
    });

    if (!backendRes.ok) {
      const errorText = await backendRes.text();
      return new Response(errorText, {
        status: backendRes.status,
        headers: {
          "Content-Type": "application/json",
          ...(backendRes.headers.get("retry-after")
            ? { "Retry-After": backendRes.headers.get("retry-after")! }
            : {}),
        },
      });
    }

    return new Response(backendRes.body, {
      status: 200,
      headers: {
        "Content-Type": "text/event-stream; charset=utf-8",
        "Cache-Control": "no-cache, no-transform",
        Connection: "keep-alive",
      },
    });
  } catch (err) {
    const error = err instanceof Error ? err.message : "Failed to proxy stream to backend";
    return new Response(JSON.stringify({ error }), {
      status: 502,
      headers: { "Content-Type": "application/json" },
    });
  }
}
