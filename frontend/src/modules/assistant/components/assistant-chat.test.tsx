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
});
