import { afterEach, describe, expect, it, vi } from "vitest";

describe("buildAssistantChatRequest", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.resetModules();
    delete (globalThis as Record<string, unknown>).window;
    delete (globalThis as Record<string, unknown>).document;
  });

  it("omits the turnstile token when no site key is configured", async () => {
    vi.stubEnv("NEXT_PUBLIC_TURNSTILE_SITE_KEY", "");
    const { buildAssistantChatRequest } = await import("./turnstile");

    const request = await buildAssistantChatRequest("hello");

    expect(request).toEqual({ message: "hello" });
    expect("turnstileToken" in request).toBe(false);
  });

  it("omits the turnstile token during server-side rendering", async () => {
    vi.stubEnv("NEXT_PUBLIC_TURNSTILE_SITE_KEY", "test-site-key");
    const { buildAssistantChatRequest } = await import("./turnstile");

    const request = await buildAssistantChatRequest("hello", "vextis");

    expect(request).toEqual({ message: "hello", slug: "vextis" });
  });

  it("forwards the acquired token when turnstile succeeds", async () => {
    vi.stubEnv("NEXT_PUBLIC_TURNSTILE_SITE_KEY", "test-site-key");

    const remove = vi.fn();
    const container = { style: {}, remove: vi.fn() };
    (globalThis as Record<string, unknown>).document = {
      createElement: vi.fn().mockReturnValue(container),
      body: { appendChild: vi.fn() },
    };
    (globalThis as Record<string, unknown>).window = {
      turnstile: {
        render: vi.fn((_el: unknown, options: { callback: (token: string) => void }) => {
          options.callback("token-123");
          return "widget-1";
        }),
        execute: vi.fn(),
        remove,
      },
    };

    const { buildAssistantChatRequest } = await import("./turnstile");

    const request = await buildAssistantChatRequest("hello");

    expect(request.turnstileToken).toBe("token-123");
  });

  it("fails closed without a token when the widget errors", async () => {
    vi.stubEnv("NEXT_PUBLIC_TURNSTILE_SITE_KEY", "test-site-key");

    const container = { style: {}, remove: vi.fn() };
    (globalThis as Record<string, unknown>).document = {
      createElement: vi.fn().mockReturnValue(container),
      body: { appendChild: vi.fn() },
    };
    (globalThis as Record<string, unknown>).window = {
      turnstile: {
        render: vi.fn((_el: unknown, options: { "error-callback": () => void }) => {
          options["error-callback"]();
          return "widget-1";
        }),
        execute: vi.fn(),
        remove: vi.fn(),
      },
    };

    const { buildAssistantChatRequest } = await import("./turnstile");

    const request = await buildAssistantChatRequest("hello");

    expect("turnstileToken" in request).toBe(false);
  });
});
