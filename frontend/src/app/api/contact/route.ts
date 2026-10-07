import { getBackendBaseUrl } from "@/modules/portfolio/api";

const MAX_BODY_BYTES = 64 * 1024; // 64 KiB

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
    // 1. Strict 64 KiB body limit before unbounded buffering
    const contentLength = request.headers.get("content-length");
    if (contentLength && parseInt(contentLength, 10) > MAX_BODY_BYTES) {
      return new Response(
        JSON.stringify({ error: "Request body exceeds the maximum size of 64 KiB." }),
        { status: 413, headers: { "Content-Type": "application/json" } }
      );
    }

    if (!request.body) {
      return new Response(
        JSON.stringify({ error: "Request body is required." }),
        { status: 400, headers: { "Content-Type": "application/json" } }
      );
    }

    const reader = request.body.getReader();
    const chunks: Uint8Array[] = [];
    let totalBytes = 0;

    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      if (value) {
        totalBytes += value.byteLength;
        if (totalBytes > MAX_BODY_BYTES) {
          await reader.cancel();
          return new Response(
            JSON.stringify({ error: "Request body exceeds the maximum size of 64 KiB." }),
            { status: 413, headers: { "Content-Type": "application/json" } }
          );
        }
        chunks.push(value);
      }
    }

    const combined = new Uint8Array(totalBytes);
    let offset = 0;
    for (const chunk of chunks) {
      combined.set(chunk, offset);
      offset += chunk.byteLength;
    }
    const body = new TextDecoder().decode(combined);

    const backendUrl = `${getBackendBaseUrl()}/v1/contact`;

    // 2. Identity signing
    const identityHeaders: Record<string, string> = {};
    const identitySecret = process.env.ASSISTANT_PROXY_IDENTITY_SECRET;

    if (!identitySecret && process.env.NODE_ENV === "production") {
      return new Response(
        JSON.stringify({ error: "Contact identity boundary is not configured." }),
        { status: 503, headers: { "Content-Type": "application/json" } }
      );
    }

    const visitorId = request.headers.get("CF-Connecting-IP")?.trim();
    if (identitySecret && visitorId) {
      identityHeaders["X-Client-Key"] = visitorId;
      identityHeaders["X-Client-Key-Proof"] = await hmacSha256Hex(identitySecret, visitorId);
    }

    // 3. Dispatch to backend
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
    const error = err instanceof Error ? err.message : "Failed to proxy contact request to backend";
    return new Response(JSON.stringify({ error }), {
      status: 502,
      headers: { "Content-Type": "application/json" },
    });
  }
}
