import type { AssistantChatRequest, AssistantCitation, AssistantGroundingStatus } from "./model";

export interface StreamCallbacks {
  readonly onStatus?: (status: AssistantGroundingStatus) => void;
  readonly onToken?: (text: string) => void;
  readonly onCitation?: (citation: AssistantCitation) => void;
  readonly onError?: (errorMessage: string) => void;
  readonly onDone?: () => void;
}

export async function streamAssistantChat(
  request: AssistantChatRequest,
  callbacks: StreamCallbacks,
  signal?: AbortSignal
): Promise<void> {
  try {
    const res = await fetch("/api/assistant/chat/stream", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Accept: "text/event-stream",
      },
      body: JSON.stringify(request),
      signal,
    });

    if (res.status === 429) {
      callbacks.onError?.("Rate limit exceeded (max 10 questions/min). Please wait a moment before trying again.");
      return;
    }

    if (res.status === 403) {
      callbacks.onError?.("Human verification check failed or expired.");
      return;
    }

    if (!res.ok) {
      const errorJson = (await res.json().catch(() => null)) as { error?: string } | null;
      callbacks.onError?.(errorJson?.error || `Request failed with status ${res.status}`);
      return;
    }

    if (!res.body) {
      callbacks.onError?.("ReadableStream is not supported by the response.");
      return;
    }

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";

    while (true) {
      const { value, done } = await reader.read();
      if (done) {
        break;
      }

      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split("\n");
      buffer = lines.pop() || "";

      let currentEvent = "";
      for (const line of lines) {
        const trimmed = line.trim();
        if (trimmed.startsWith("event:")) {
          currentEvent = trimmed.slice(6).trim();
        } else if (trimmed.startsWith("data:")) {
          const dataStr = trimmed.slice(5).trim();
          try {
            const data = JSON.parse(dataStr);
            if (currentEvent === "status" && data.groundingStatus) {
              callbacks.onStatus?.(data.groundingStatus);
            } else if (currentEvent === "token" && typeof data.text === "string") {
              callbacks.onToken?.(data.text);
            } else if (currentEvent === "citation" && data.citation) {
              callbacks.onCitation?.(data.citation);
            } else if (currentEvent === "error" && data.error) {
              callbacks.onError?.(data.error);
            } else if (currentEvent === "done") {
              callbacks.onDone?.();
            }
          } catch {
            // Ignore parse errors on malformed chunks
          }
        }
      }
    }

    callbacks.onDone?.();
  } catch (err) {
    if (signal?.aborted) {
      return;
    }
    const message = err instanceof Error ? err.message : "Failed to connect to assistant stream";
    callbacks.onError?.(message);
  }
}
