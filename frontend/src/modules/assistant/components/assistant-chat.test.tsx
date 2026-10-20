// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const streamAssistantChat = vi.fn();
const prepareAssistantChatRequest = vi.fn();

vi.mock("../api", () => ({
  streamAssistantChat: (...args: unknown[]) => streamAssistantChat(...args),
}));

vi.mock("../turnstile", () => ({
  prepareAssistantChatRequest: (...args: unknown[]) => prepareAssistantChatRequest(...args),
}));

import { AssistantChat } from "./assistant-chat";
import { AssistantPreview } from "./assistant-preview";
import { MODEL_PROCESSING_NOTICE_VERSION } from "../model";

async function submitQuestion(text: string) {
  const input = screen.getByLabelText("Your question");
  fireEvent.change(input, { target: { value: text } });
  fireEvent.submit(input.closest("form")!);
  return input as HTMLInputElement;
}

describe("AssistantChat request preparation failures", () => {
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it("introduces the assistant as R AI while retaining the portfolio owner's name", () => {
    render(<AssistantChat />);

    expect(screen.getByText("R AI")).toBeTruthy();
    expect(screen.getByText(/I am R AI,/).textContent).toContain("Rafael's systems");
    expect(screen.queryByText(/Rafael AI/)).toBeNull();
  });

  it("uses R AI in the assistant preview", () => {
    render(<AssistantPreview />);

    expect(screen.getByText("R AI")).toBeTruthy();
    expect(screen.queryByText("Rafael AI")).toBeNull();
  });

  it("restores the input, drops the placeholder, and shows a retryable error when verification fails", async () => {
    prepareAssistantChatRequest.mockResolvedValue({ kind: "verification_failed" });

    render(<AssistantChat />);
    const input = await submitQuestion("Explain the JobTY matching engine");

    const banner = await screen.findByRole("alert");
    expect(banner.textContent).toContain("Human verification could not be completed");

    await waitFor(() => expect(input.disabled).toBe(false));
    expect(streamAssistantChat).not.toHaveBeenCalled();

    // Only the welcome line and the user question remain; the placeholder
    // assistant response was removed.
    expect(screen.queryByText("Unable to complete response. Please see error below.")).toBeNull();
    expect(screen.getByText("Explain the JobTY matching engine")).toBeTruthy();
  });

  it("recovers to the same retryable state when preparation throws", async () => {
    prepareAssistantChatRequest.mockRejectedValue(new Error("unexpected failure"));

    render(<AssistantChat />);
    const input = await submitQuestion("Explain Rafael's focus areas");

    const banner = await screen.findByRole("alert");
    expect(banner.textContent).toContain("Human verification could not be completed");

    await waitFor(() => expect(input.disabled).toBe(false));
    expect(streamAssistantChat).not.toHaveBeenCalled();
  });

  function prepareSuccessfulTurn() {
    prepareAssistantChatRequest.mockImplementation(async (message, _slug, modelConsent) => ({
      kind: "ready", request: { message, ...(modelConsent ? { modelConsent } : {}) },
    }));
    streamAssistantChat.mockImplementation(async (_request, callbacks) => {
      callbacks.onToken?.("A documented response.");
      callbacks.onDone?.();
    });
  }

  it("starts unchecked, explains global standard retention, and permits deterministic questions", async () => {
    prepareSuccessfulTurn();
    render(<AssistantChat />);
    const checkbox = screen.getByRole("checkbox") as HTMLInputElement;
    expect(checkbox.checked).toBe(false);
    expect(checkbox.required).toBe(false);
    expect(screen.getByText(/Optional Google AI:/).textContent).toContain("not zero retention");
    expect(screen.getByText(/Optional Google AI:/).textContent).toContain("no fixed-country guarantee");
    expect(screen.getByRole("link", { name: /Google's processing/ }).getAttribute("href")).toContain("docs.cloud.google.com/");
    expect(screen.getByRole("link", { name: "Contact section" }).getAttribute("href")).toBe("#contact");

    await submitQuestion("Tell me about Expinn");
    await waitFor(() => expect(streamAssistantChat).toHaveBeenCalledTimes(1));
    expect(streamAssistantChat.mock.calls[0][0]).toEqual({ message: "Tell me about Expinn" });
  });

  it("attaches only current affirmative consent and allows withdrawal before the next turn", async () => {
    prepareSuccessfulTurn();
    render(<AssistantChat />);
    const checkbox = screen.getByRole("checkbox") as HTMLInputElement;
    fireEvent.click(checkbox);
    await submitQuestion("First question");
    await waitFor(() => expect(streamAssistantChat).toHaveBeenCalledTimes(1));
    expect(streamAssistantChat.mock.calls[0][0]).toEqual({
      message: "First question", modelConsent: { accepted: true, noticeVersion: MODEL_PROCESSING_NOTICE_VERSION },
    });
    await waitFor(() => expect(checkbox.disabled).toBe(false));
    fireEvent.click(checkbox);
    await submitQuestion("Second question");
    await waitFor(() => expect(streamAssistantChat).toHaveBeenCalledTimes(2));
    expect(streamAssistantChat.mock.calls[1][0]).toEqual({ message: "Second question" });
  });

  it("uses the same choice for suggested questions and never persists it across remounts", async () => {
    prepareSuccessfulTurn();
    const storage = vi.spyOn(Storage.prototype, "setItem");
    const mounted = render(<AssistantChat />);
    fireEvent.click(screen.getByRole("checkbox"));
    fireEvent.click(screen.getByRole("button", { name: "What is Vextis?" }));
    await waitFor(() => expect(streamAssistantChat).toHaveBeenCalledTimes(1));
    expect(streamAssistantChat.mock.calls[0][0].modelConsent).toEqual({ accepted: true, noticeVersion: MODEL_PROCESSING_NOTICE_VERSION });
    expect(storage).not.toHaveBeenCalled();
    mounted.unmount();
    render(<AssistantChat />);
    expect((screen.getByRole("checkbox") as HTMLInputElement).checked).toBe(false);
    storage.mockRestore();
  });
});
