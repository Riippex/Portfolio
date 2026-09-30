import { getBackendBaseUrl } from "@/modules/portfolio/api";

export async function POST(request: Request): Promise<Response> {
  try {
    const body = await request.text();
    const backendUrl = `${getBackendBaseUrl()}/v1/assistant/chat/stream`;

    const backendRes = await fetch(backendUrl, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Accept: "text/event-stream",
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
