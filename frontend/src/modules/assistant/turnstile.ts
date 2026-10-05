import type { AssistantChatRequest } from "./model";

const TURNSTILE_SCRIPT_URL =
  "https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit";

const TOKEN_ACQUISITION_TIMEOUT_MS = 90_000;
const SCRIPT_LOAD_TIMEOUT_MS = 15_000;

interface TurnstileRenderOptions {
  sitekey: string;
  execution: "render" | "execute";
  appearance: "always" | "execute" | "interaction-only";
  callback: (token: string) => void;
  "error-callback": () => void;
  "timeout-callback": () => void;
  "expired-callback": () => void;
}

interface TurnstileApi {
  render: (container: HTMLElement, options: TurnstileRenderOptions) => string;
  execute: (container: HTMLElement) => void;
  remove: (widgetId: string) => void;
}

declare global {
  interface Window {
    turnstile?: TurnstileApi;
  }
}

export type TurnstileTokenResult =
  | { readonly kind: "disabled" }
  | { readonly kind: "token"; readonly token: string }
  | { readonly kind: "failed" };

export type AssistantRequestPreparation =
  | { readonly kind: "ready"; readonly request: AssistantChatRequest }
  | { readonly kind: "verification_failed" };

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

    const fail = (error: Error) => {
      clearTimeout(timer);
      scriptPromise = null;
      script.remove();
      reject(error);
    };

    const timer = setTimeout(
      () => fail(new Error("Turnstile script load timed out")),
      SCRIPT_LOAD_TIMEOUT_MS
    );

    script.onload = () => {
      clearTimeout(timer);
      resolve();
    };
    script.onerror = () => fail(new Error("Turnstile script failed to load"));
    document.head.appendChild(script);
  });
  return scriptPromise;
}

export async function acquireTurnstileToken(): Promise<TurnstileTokenResult> {
  const siteKey = getTurnstileSiteKey();
  if (!siteKey || typeof window === "undefined") {
    return { kind: "disabled" };
  }

  if (!window.turnstile) {
    try {
      await loadTurnstileScript();
    } catch {
      return { kind: "failed" };
    }
  }

  const turnstile = window.turnstile;
  if (!turnstile) {
    return { kind: "failed" };
  }

  return new Promise<TurnstileTokenResult>((resolve) => {
    const container = document.createElement("div");
    container.className = "turnstile-challenge";
    document.body.appendChild(container);

    let widgetId = "";
    let settled = false;
    const finish = (result: TurnstileTokenResult) => {
      if (settled) {
        return;
      }
      settled = true;
      clearTimeout(timer);
      if (widgetId) {
        try {
          turnstile.remove(widgetId);
        } catch {
          // Widget cleanup is best-effort and idempotent.
        }
      }
      container.remove();
      resolve(result);
    };

    const timer = setTimeout(() => finish({ kind: "failed" }), TOKEN_ACQUISITION_TIMEOUT_MS);

    try {
      widgetId = turnstile.render(container, {
        sitekey: siteKey,
        execution: "execute",
        appearance: "execute",
        callback: (token) => finish({ kind: "token", token }),
        "error-callback": () => finish({ kind: "failed" }),
        "timeout-callback": () => finish({ kind: "failed" }),
        "expired-callback": () => finish({ kind: "failed" }),
      });
      turnstile.execute(container);
    } catch {
      finish({ kind: "failed" });
    }
  });
}

export async function prepareAssistantChatRequest(
  message: string,
  slug?: string
): Promise<AssistantRequestPreparation> {
  const tokenResult = await acquireTurnstileToken();
  if (tokenResult.kind === "failed") {
    return { kind: "verification_failed" };
  }

  return {
    kind: "ready",
    request: {
      message,
      ...(slug ? { slug } : {}),
      ...(tokenResult.kind === "token" ? { turnstileToken: tokenResult.token } : {}),
    },
  };
}
