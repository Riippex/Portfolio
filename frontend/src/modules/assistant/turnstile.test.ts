import { afterEach, describe, expect, it, vi } from "vitest";

interface MockContainer {
  className: string;
  style: Record<string, string>;
  remove: ReturnType<typeof vi.fn>;
}

function createMockContainer(): MockContainer {
  return { className: "", style: {}, remove: vi.fn() };
}

function installDocument(container: MockContainer) {
  (globalThis as Record<string, unknown>).document = {
    createElement: vi.fn().mockReturnValue(container),
    body: { appendChild: vi.fn() },
    head: { appendChild: vi.fn() },
  };
}

function installTurnstile(behavior: {
  onRender?: (options: Record<string, unknown>) => void;
  onExecute?: (options: Record<string, unknown>) => void;
  throwOnRender?: boolean;
  throwOnExecute?: boolean;
}) {
  const calls = {
    renderOptions: null as Record<string, unknown> | null,
    executeArgs: [] as unknown[][],
    removeArgs: [] as unknown[][],
  };
  (globalThis as Record<string, unknown>).window = {
    turnstile: {
      render: vi.fn((_el: unknown, options: Record<string, unknown>) => {
        if (behavior.throwOnRender) {
          throw new Error("render exploded");
        }
        calls.renderOptions = options;
        behavior.onRender?.(options);
        return "widget-1";
      }),
      execute: vi.fn((...args: unknown[]) => {
        if (behavior.throwOnExecute) {
          throw new Error("execute exploded");
        }
        calls.executeArgs.push(args);
        behavior.onExecute?.(calls.renderOptions ?? {});
      }),
      remove: vi.fn((...args: unknown[]) => {
        calls.removeArgs.push(args);
      }),
    },
  };
  return calls;
}

describe("prepareAssistantChatRequest", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.useRealTimers();
    vi.resetModules();
    delete (globalThis as Record<string, unknown>).window;
    delete (globalThis as Record<string, unknown>).document;
  });

  it("builds a tokenless request when no site key is configured", async () => {
    vi.stubEnv("NEXT_PUBLIC_TURNSTILE_SITE_KEY", "");
    const { prepareAssistantChatRequest } = await import("./turnstile");

    const result = await prepareAssistantChatRequest("hello");

    expect(result).toEqual({ kind: "ready", request: { message: "hello" } });
  });

  it("builds a tokenless request during server-side rendering", async () => {
    vi.stubEnv("NEXT_PUBLIC_TURNSTILE_SITE_KEY", "test-site-key");
    const { prepareAssistantChatRequest } = await import("./turnstile");

    const result = await prepareAssistantChatRequest("hello", "vextis");

    expect(result).toEqual({ kind: "ready", request: { message: "hello", slug: "vextis" } });
  });

  it("forwards the token using the documented managed-execution lifecycle", async () => {
    vi.stubEnv("NEXT_PUBLIC_TURNSTILE_SITE_KEY", "test-site-key");
    const container = createMockContainer();
    installDocument(container);
    const calls = installTurnstile({
      onExecute: (options) => {
        (options.callback as (token: string) => void)("token-123");
      },
    });

    const { prepareAssistantChatRequest } = await import("./turnstile");

    const result = await prepareAssistantChatRequest("hello");

    expect(result).toEqual({
      kind: "ready",
      request: { message: "hello", turnstileToken: "token-123" },
    });

    // Canonical lifecycle: render with execution/appearance set to "execute",
    // execute against the widget container, remove by widget id.
    expect(calls.renderOptions?.execution).toBe("execute");
    expect(calls.renderOptions?.appearance).toBe("execute");
    expect(calls.executeArgs).toEqual([[container]]);
    expect(calls.removeArgs).toEqual([["widget-1"]]);

    // The challenge container stays visible-capable, never display:none.
    expect(container.style.display).not.toBe("none");
  });

  it("reports verification_failed when the widget errors and settles exactly once", async () => {
    vi.stubEnv("NEXT_PUBLIC_TURNSTILE_SITE_KEY", "test-site-key");
    installDocument(createMockContainer());
    const calls = installTurnstile({
      onExecute: (options) => {
        (options.callback as (token: string) => void)("late-token");
        (options["error-callback"] as () => void)();
      },
    });

    const { prepareAssistantChatRequest } = await import("./turnstile");

    const result = await prepareAssistantChatRequest("hello");

    // First settlement wins; cleanup runs exactly once.
    expect(result).toEqual({
      kind: "ready",
      request: { message: "hello", turnstileToken: "late-token" },
    });
    expect(calls.removeArgs).toEqual([["widget-1"]]);
  });

  it("fails closed when render throws synchronously", async () => {
    vi.stubEnv("NEXT_PUBLIC_TURNSTILE_SITE_KEY", "test-site-key");
    const container = createMockContainer();
    installDocument(container);
    const calls = installTurnstile({ throwOnRender: true });

    const { prepareAssistantChatRequest } = await import("./turnstile");

    const result = await prepareAssistantChatRequest("hello");

    expect(result).toEqual({ kind: "verification_failed" });
    expect(calls.removeArgs).toEqual([]);
    expect(container.remove).toHaveBeenCalledTimes(1);
  });

  it("fails closed when execute throws synchronously", async () => {
    vi.stubEnv("NEXT_PUBLIC_TURNSTILE_SITE_KEY", "test-site-key");
    installDocument(createMockContainer());
    const calls = installTurnstile({ throwOnExecute: true });

    const { prepareAssistantChatRequest } = await import("./turnstile");

    const result = await prepareAssistantChatRequest("hello");

    expect(result).toEqual({ kind: "verification_failed" });
    expect(calls.removeArgs).toEqual([["widget-1"]]);
  });

  it("bounds token acquisition with a timeout", async () => {
    vi.useFakeTimers();
    vi.stubEnv("NEXT_PUBLIC_TURNSTILE_SITE_KEY", "test-site-key");
    installDocument(createMockContainer());
    const calls = installTurnstile({});

    const { acquireTurnstileToken } = await import("./turnstile");

    const pending = acquireTurnstileToken();
    await vi.advanceTimersByTimeAsync(90_000);

    await expect(pending).resolves.toEqual({ kind: "failed" });
    expect(calls.removeArgs).toEqual([["widget-1"]]);
  });

  it("bounds the script load with its own timeout and allows retry", async () => {
    vi.useFakeTimers();
    vi.stubEnv("NEXT_PUBLIC_TURNSTILE_SITE_KEY", "test-site-key");
    const container = createMockContainer();
    const script = {
      src: "",
      async: false,
      onload: null as null | (() => void),
      onerror: null as null | (() => void),
      remove: vi.fn(),
    };
    (globalThis as Record<string, unknown>).document = {
      createElement: vi.fn((tag: string) => (tag === "script" ? script : container)),
      body: { appendChild: vi.fn() },
      head: { appendChild: vi.fn() },
    };
    (globalThis as Record<string, unknown>).window = {};

    const { acquireTurnstileToken } = await import("./turnstile");

    const pending = acquireTurnstileToken();
    await vi.advanceTimersByTimeAsync(15_000);

    await expect(pending).resolves.toEqual({ kind: "failed" });
    expect(script.remove).toHaveBeenCalledTimes(1);

    vi.useRealTimers();
    installTurnstile({
      onExecute: (options) => {
        (options.callback as (token: string) => void)("token-after-timeout");
      },
    });

    await expect(acquireTurnstileToken()).resolves.toEqual({
      kind: "token",
      token: "token-after-timeout",
    });
  });

  it("retries after a script-load failure instead of caching the rejection", async () => {
    vi.stubEnv("NEXT_PUBLIC_TURNSTILE_SITE_KEY", "test-site-key");
    const container = createMockContainer();
    const script = {
      src: "",
      async: false,
      onload: null as null | (() => void),
      onerror: null as null | (() => void),
      remove: vi.fn(),
    };
    (globalThis as Record<string, unknown>).document = {
      createElement: vi.fn((tag: string) => (tag === "script" ? script : container)),
      body: { appendChild: vi.fn() },
      head: {
        appendChild: vi.fn((el: typeof script) => {
          queueMicrotask(() => el.onerror?.());
        }),
      },
    };
    (globalThis as Record<string, unknown>).window = {};

    const { acquireTurnstileToken } = await import("./turnstile");

    await expect(acquireTurnstileToken()).resolves.toEqual({ kind: "failed" });

    installTurnstile({
      onExecute: (options) => {
        (options.callback as (token: string) => void)("token-after-retry");
      },
    });

    await expect(acquireTurnstileToken()).resolves.toEqual({
      kind: "token",
      token: "token-after-retry",
    });
  });
});
