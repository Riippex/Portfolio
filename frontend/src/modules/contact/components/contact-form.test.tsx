// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const sendContactMessage = vi.fn();
const acquireTurnstileToken = vi.fn();

vi.mock("../api", () => ({
  sendContactMessage: (...args: unknown[]) => sendContactMessage(...args),
}));

vi.mock("@/modules/assistant/turnstile", () => ({
  acquireTurnstileToken: () => acquireTurnstileToken(),
}));

import { ContactForm } from "./contact-form";

describe("ContactForm component", () => {
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it("renders all form elements with initial disabled submit state", () => {
    render(<ContactForm />);

    expect(screen.getByLabelText(/your name/i)).toBeDefined();
    expect(screen.getByLabelText(/reply email/i)).toBeDefined();
    expect(screen.getByLabelText(/^message/i)).toBeDefined();
    expect(screen.getByRole("checkbox")).toBeDefined();

    const submitBtn = screen.getByRole("button", { name: /send message/i });
    expect(submitBtn.hasAttribute("disabled")).toBe(true);
  });

  it("enables submit button only when name, email, valid message, and consent are provided", () => {
    render(<ContactForm />);

    const nameInput = screen.getByLabelText(/your name/i);
    const emailInput = screen.getByLabelText(/reply email/i);
    const messageInput = screen.getByLabelText(/^message/i);
    const consentCheckbox = screen.getByRole("checkbox");
    const submitBtn = screen.getByRole("button", { name: /send message/i });

    expect(submitBtn.hasAttribute("disabled")).toBe(true);

    fireEvent.change(nameInput, { target: { value: "Jane Doe" } });
    expect(submitBtn.hasAttribute("disabled")).toBe(true);

    fireEvent.change(emailInput, { target: { value: "jane@example.com" } });
    expect(submitBtn.hasAttribute("disabled")).toBe(true);

    // Message too short (<10 chars)
    fireEvent.change(messageInput, { target: { value: "Short" } });
    expect(submitBtn.hasAttribute("disabled")).toBe(true);

    // Message valid (>=10 chars)
    fireEvent.change(messageInput, { target: { value: "Hello Rafael, inquiry about your AI work." } });
    expect(submitBtn.hasAttribute("disabled")).toBe(true);

    // Consent checked
    fireEvent.click(consentCheckbox);
    expect(submitBtn.hasAttribute("disabled")).toBe(false);
  });

  it("submits message successfully and renders delivered status", async () => {
    acquireTurnstileToken.mockResolvedValueOnce({ kind: "token", token: "valid-turnstile-token" });
    sendContactMessage.mockResolvedValueOnce({
      ok: true,
      data: { status: "delivered", outcome: "delivered" },
    });

    render(<ContactForm />);

    fireEvent.change(screen.getByLabelText(/your name/i), { target: { value: "Jane Doe" } });
    fireEvent.change(screen.getByLabelText(/reply email/i), { target: { value: "jane@example.com" } });
    fireEvent.change(screen.getByLabelText(/^message/i), { target: { value: "Hello Rafael, inquiry about your work." } });
    fireEvent.click(screen.getByRole("checkbox"));

    fireEvent.click(screen.getByRole("button", { name: /send message/i }));

    expect(await screen.findByText(/message delivered/i)).toBeDefined();
    expect(screen.getByText(/zero application persistence/i)).toBeDefined();
    expect(sendContactMessage).toHaveBeenCalledWith({
      name: "Jane Doe",
      email: "jane@example.com",
      message: "Hello Rafael, inquiry about your work.",
      consent: true,
      turnstileToken: "valid-turnstile-token",
    });

    // Reset works
    fireEvent.click(screen.getByRole("button", { name: /send another message/i }));
    const resetNameInput = screen.getByLabelText(/your name/i) as HTMLInputElement;
    expect(resetNameInput.value).toBe("");
  });

  it("handles queued status successfully", async () => {
    acquireTurnstileToken.mockResolvedValueOnce({ kind: "disabled" });
    sendContactMessage.mockResolvedValueOnce({
      ok: true,
      data: { status: "queued", outcome: "queued" },
    });

    render(<ContactForm />);

    fireEvent.change(screen.getByLabelText(/your name/i), { target: { value: "Jane Doe" } });
    fireEvent.change(screen.getByLabelText(/reply email/i), { target: { value: "jane@example.com" } });
    fireEvent.change(screen.getByLabelText(/^message/i), { target: { value: "Hello Rafael, inquiry about your work." } });
    fireEvent.click(screen.getByRole("checkbox"));

    fireEvent.click(screen.getByRole("button", { name: /send message/i }));

    expect(await screen.findByText(/message queued for delivery/i)).toBeDefined();
  });

  it("displays service unavailable banner on 503 response", async () => {
    acquireTurnstileToken.mockResolvedValueOnce({ kind: "disabled" });
    sendContactMessage.mockResolvedValueOnce({
      ok: false,
      status: 503,
      error: "Contact service is temporarily unavailable.",
    });

    render(<ContactForm />);

    fireEvent.change(screen.getByLabelText(/your name/i), { target: { value: "Jane Doe" } });
    fireEvent.change(screen.getByLabelText(/reply email/i), { target: { value: "jane@example.com" } });
    fireEvent.change(screen.getByLabelText(/^message/i), { target: { value: "Hello Rafael, inquiry about your work." } });
    fireEvent.click(screen.getByRole("checkbox"));

    fireEvent.click(screen.getByRole("button", { name: /send message/i }));

    expect(await screen.findByText(/service unavailable/i)).toBeDefined();
    expect(screen.getByText(/contact service is temporarily unavailable/i)).toBeDefined();
  });

  it("shows error and does not submit when human verification fails", async () => {
    acquireTurnstileToken.mockResolvedValueOnce({ kind: "failed" });

    render(<ContactForm />);

    fireEvent.change(screen.getByLabelText(/your name/i), { target: { value: "Jane Doe" } });
    fireEvent.change(screen.getByLabelText(/reply email/i), { target: { value: "jane@example.com" } });
    fireEvent.change(screen.getByLabelText(/^message/i), { target: { value: "Hello Rafael, inquiry about your work." } });
    fireEvent.click(screen.getByRole("checkbox"));

    fireEvent.click(screen.getByRole("button", { name: /send message/i }));

    expect(await screen.findByText(/human verification failed/i)).toBeDefined();
    expect(sendContactMessage).not.toHaveBeenCalled();
  });
});
