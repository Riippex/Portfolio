import type { AssistantChatRequest } from "./model";

const TURNSTILE_SCRIPT_URL =
  "https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit";

interface TurnstileApi {
  render: (
    container: HTMLElement,
    options: {
      sitekey: string;
      callback: (token: string) => void;
      "error-callback": () => void;
      "expired-callback": () => void;
    }
  ) => string;
  execute: (container: HTMLElement, widgetId: string) => void;
  remove: (container: HTMLElement, widgetId: string) => void;
}

declare global {
  interface Window {
    turnstile?: TurnstileApi;
  }
}

export function getTurnstileSiteKey(): string | null {
  const key = process.env.NEXT_PUBLIC_TURNSTILE_SITE_KEY;
  return key && key.trim().length > 0 ? key.trim() : null;
}

let scriptPromise: Promise<void> | null = null;

function loadTurnstileScript(): Promise<void> {
  scriptPromise ??= new Promise((resolve, reject) => {
    const script = document.createElement("script");
    script.src = TURNSTILE_SCRIPT_URL;
    script.async = true;
    script.onload = () => resolve();
    script.onerror = () => reject(new Error("Turnstile script failed to load"));
    document.head.appendChild(script);
  });
  return scriptPromise;
}

export async function acquireTurnstileToken(): Promise<string | null> {
  const siteKey = getTurnstileSiteKey();
  if (!siteKey || typeof window === "undefined") {
    return null;
  }

  if (!window.turnstile) {
    await loadTurnstileScript();
  }

  const turnstile = window.turnstile;
  if (!turnstile) {
    return null;
  }

  return new Promise<string | null>((resolve) => {
    const container = document.createElement("div");
    container.style.display = "none";
    document.body.appendChild(container);

    let widgetId = "";
    const finish = (token: string | null) => {
      try {
        turnstile.remove(container, widgetId);
      } catch {
        // Widget cleanup is best-effort.
      }
      container.remove();
      resolve(token);
    };

    widgetId = turnstile.render(container, {
      sitekey: siteKey,
      callback: (token) => finish(token),
      "error-callback": () => finish(null),
      "expired-callback": () => finish(null),
    });
    turnstile.execute(container, widgetId);
  });
}

export async function buildAssistantChatRequest(
  message: string,
  slug?: string
): Promise<AssistantChatRequest> {
  const turnstileToken = await acquireTurnstileToken();
  return {
    message,
    ...(slug ? { slug } : {}),
    ...(turnstileToken ? { turnstileToken } : {}),
  };
}
