// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const acquireTurnstileToken = vi.fn();

vi.mock("@/modules/assistant/turnstile", () => ({
  acquireTurnstileToken: () => acquireTurnstileToken(),
}));

// The real form and the real API client run here; only fetch (the network) is faked.
import { ContactForm } from "./contact-form";

function fillAndConsent() {
  fireEvent.change(screen.getByLabelText(/your name/i), { target: { value: "Jane Doe" } });
  fireEvent.change(screen.getByLabelText(/reply email/i), { target: { value: "jane@example.com" } });
  fireEvent.change(screen.getByLabelText(/^message/i), {
    target: { value: "Hello Rafael, inquiry about your work." },
  });
  fireEvent.click(screen.getByRole("checkbox"));
}

function respondWith(status: number, body: unknown) {
  const fetchMock = vi.fn<typeof fetch>(async () =>
    new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } })
  );
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

describe("ContactForm flow with a fake network", () => {
  beforeEach(() => {
    let counter = 0;
    acquireTurnstileToken.mockImplementation(async () => ({ kind: "token", token: `token-${++counter}` }));
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it("shows queued only for the documented queued payload", async () => {
    respondWith(200, { status: "queued", outcome: "queued" });
    render(<ContactForm />);
    fillAndConsent();
    fireEvent.click(screen.getByRole("button", { name: /send message/i }));

    expect(await screen.findByText(/message queued for delivery/i)).toBeDefined();
  });

  it("shows a failure, not a queued message, for HTTP 200 with a failed status", async () => {
    respondWith(200, { status: "failed", outcome: "delivery_unconfirmed" });
    render(<ContactForm />);
    fillAndConsent();
    fireEvent.click(screen.getByRole("button", { name: /send message/i }));

    expect(await screen.findByText(/submission failed/i)).toBeDefined();
    expect(screen.getByText(/unexpected response/i)).toBeDefined();
    expect(screen.queryByText(/queued for delivery/i)).toBeNull();
    expect(screen.queryByText(/message delivered/i)).toBeNull();
  });

  it.each([
    ["an unknown status", { status: "sent", outcome: "sent" }],
    ["a malformed payload", { ok: true }],
    ["an empty payload", {}],
  ])("shows a failure for HTTP 200 with %s", async (_label, body) => {
    respondWith(200, body);
    render(<ContactForm />);
    fillAndConsent();
    fireEvent.click(screen.getByRole("button", { name: /send message/i }));

    expect(await screen.findByText(/submission failed/i)).toBeDefined();
    expect(screen.queryByText(/queued for delivery/i)).toBeNull();
    expect(screen.queryByText(/message delivered/i)).toBeNull();
  });

  it("sends once for a double submission", async () => {
    const fetchMock = respondWith(200, { status: "delivered", outcome: "delivered" });
    render(<ContactForm />);
    fillAndConsent();

    const form = screen.getByRole("button", { name: /send message/i }).closest("form")!;
    fireEvent.submit(form);
    fireEvent.submit(form);

    expect(await screen.findByText(/message delivered/i)).toBeDefined();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(acquireTurnstileToken).toHaveBeenCalledTimes(1);
  });

  it("does not retry automatically after an ambiguous failure and takes a fresh token per manual attempt", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const fetchMock = respondWith(502, { error: "Contact message delivery failed.", outcome: "timeout" });
    render(<ContactForm />);
    fillAndConsent();

    fireEvent.click(screen.getByRole("button", { name: /send message/i }));
    expect(await screen.findByText(/submission failed/i)).toBeDefined();
    expect(fetchMock).toHaveBeenCalledTimes(1);

    // Nothing happens on its own, however long the visitor waits.
    await vi.advanceTimersByTimeAsync(10 * 60 * 1000);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(acquireTurnstileToken).toHaveBeenCalledTimes(1);

    // A second attempt is a deliberate user action and gets its own verification token.
    fireEvent.click(screen.getByRole("button", { name: /send message/i }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    expect(acquireTurnstileToken).toHaveBeenCalledTimes(2);

    const tokens = fetchMock.mock.calls.map((call) => {
      const init = call[1] as RequestInit;
      return (JSON.parse(String(init.body)) as { turnstileToken: string }).turnstileToken;
    });
    expect(tokens).toEqual(["token-1", "token-2"]);
  });

  it("keeps the draft only in component memory", async () => {
    const setItem = vi.spyOn(Storage.prototype, "setItem");
    respondWith(200, { status: "delivered", outcome: "delivered" });
    render(<ContactForm />);
    fillAndConsent();
    fireEvent.click(screen.getByRole("button", { name: /send message/i }));

    expect(await screen.findByText(/message delivered/i)).toBeDefined();
    expect(setItem).not.toHaveBeenCalled();
    setItem.mockRestore();
  });
});
