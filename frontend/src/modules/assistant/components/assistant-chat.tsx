"use client";

import { useState, useRef, useEffect, useTransition } from "react";
import type { AssistantChatMessage, AssistantCitation, AssistantGroundingStatus } from "../model";
import { streamAssistantChat } from "../api";
import Link from "next/link";

const INITIAL_MESSAGES: readonly AssistantChatMessage[] = [
  {
    id: "welcome",
    role: "assistant",
    content:
      "I am Rafael AI, an interactive portfolio assistant grounded exclusively in versioned public evidence. Ask about Rafael's systems, focus areas, or architecture patterns.",
    groundingStatus: "grounded",
  },
];

const SUGGESTIONS = [
  "What is Vextis?",
  "Explain Rafael's focus areas",
  "What autonomous agent experience is documented?",
];

export function AssistantChat() {
  const [messages, setMessages] = useState<readonly AssistantChatMessage[]>(INITIAL_MESSAGES);
  const [input, setInput] = useState("");
  const [isStreaming, setIsStreaming] = useState(false);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [, startTransition] = useTransition();

  const terminalRef = useRef<HTMLDivElement>(null);
  const abortControllerRef = useRef<AbortController | null>(null);
  const messageCounterRef = useRef(0);

  useEffect(() => {
    if (terminalRef.current) {
      terminalRef.current.scrollTop = terminalRef.current.scrollHeight;
    }
  }, [messages]);

  const handleSendMessage = (textToSend: string) => {
    const trimmed = textToSend.trim();
    if (!trimmed || isStreaming) {
      return;
    }

    setErrorMessage(null);
    messageCounterRef.current += 1;
    const userMessageId = `user-${messageCounterRef.current}`;
    const assistantMessageId = `assistant-${messageCounterRef.current}`;

    const userMessage: AssistantChatMessage = {
      id: userMessageId,
      role: "user",
      content: trimmed,
    };

    const initialAssistantMessage: AssistantChatMessage = {
      id: assistantMessageId,
      role: "assistant",
      content: "",
      isStreaming: true,
      citations: [],
    };

    setMessages((prev) => [...prev, userMessage, initialAssistantMessage]);
    setInput("");
    setIsStreaming(true);

    const controller = new AbortController();
    abortControllerRef.current = controller;

    let accumulatedContent = "";
    let accumulatedStatus: AssistantGroundingStatus = "grounded";
    const accumulatedCitations: AssistantCitation[] = [];

    streamAssistantChat(
      { message: trimmed },
      {
        onStatus: (status) => {
          accumulatedStatus = status;
          startTransition(() => {
            setMessages((prev) =>
              prev.map((msg) =>
                msg.id === assistantMessageId
                  ? { ...msg, groundingStatus: status }
                  : msg
              )
            );
          });
        },
        onToken: (token) => {
          accumulatedContent += (accumulatedContent ? " " : "") + token;
          startTransition(() => {
            setMessages((prev) =>
              prev.map((msg) =>
                msg.id === assistantMessageId
                  ? { ...msg, content: accumulatedContent }
                  : msg
              )
            );
          });
        },
        onCitation: (citation) => {
          accumulatedCitations.push(citation);
          startTransition(() => {
            setMessages((prev) =>
              prev.map((msg) =>
                msg.id === assistantMessageId
                  ? { ...msg, citations: [...accumulatedCitations] }
                  : msg
              )
            );
          });
        },
        onError: (err) => {
          setErrorMessage(err);
          setIsStreaming(false);
          startTransition(() => {
            setMessages((prev) =>
              prev.map((msg) =>
                msg.id === assistantMessageId
                  ? {
                      ...msg,
                      isStreaming: false,
                      content: msg.content || "Unable to complete response. Please see error below.",
                    }
                  : msg
              )
            );
          });
        },
        onDone: () => {
          setIsStreaming(false);
          startTransition(() => {
            setMessages((prev) =>
              prev.map((msg) =>
                msg.id === assistantMessageId
                  ? {
                      ...msg,
                      isStreaming: false,
                      content: accumulatedContent || msg.content,
                      groundingStatus: accumulatedStatus,
                      citations: accumulatedCitations,
                    }
                  : msg
              )
            );
          });
        },
      },
      controller.signal
    );
  };

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    handleSendMessage(input);
  };

  return (
    <div className="assistant-card" aria-label="Grounded AI Assistant">
      <div className="assistant-bar">
        <span>Grounded session · {isStreaming ? "Streaming..." : "Connected"}</span>
        <span>Stateless · Rate limited</span>
      </div>

      <div className="terminal assistant-terminal-scroll" ref={terminalRef} role="log" aria-live="polite">
        {messages.map((msg) => (
          <div
            key={msg.id}
            className={`terminal-line ${msg.role === "assistant" ? "answer" : ""}`}
          >
            <span className="label">{msg.role === "user" ? "You" : "Rafael AI"}</span>
            <div className="message-body">
              <span className="message-text">
                {msg.content}
                {msg.isStreaming && <span className="stream-cursor" aria-hidden="true">▋</span>}
              </span>

              {msg.groundingStatus && (
                <div className="grounding-badge-row">
                  <span
                    className={`status-pill ${
                      msg.groundingStatus === "grounded" ? "verified" : "pending"
                    }`}
                  >
                    {msg.groundingStatus === "grounded" ? "Evidence Grounded" : "Not Documented"}
                  </span>
                </div>
              )}

              {msg.citations && msg.citations.length > 0 && (
                <div className="citations-list" aria-label="Cited sources">
                  <span className="citations-label">Sources:</span>
                  {msg.citations.map((c) => (
                    <div className="citation-chip" key={c.chunkId}>
                      <Link href={`/projects/${c.slug}`} className="citation-link">
                        {c.title} — {c.sectionHeading}
                      </Link>
                      <span className={`status-pill ${c.evidenceStatus}`}>
                        {c.evidenceStatus}
                      </span>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </div>
        ))}
      </div>

      {errorMessage && (
        <div className="assistant-error-banner" role="alert">
          <span>{errorMessage}</span>
          <button type="button" onClick={() => setErrorMessage(null)} className="error-dismiss">
            ✕
          </button>
        </div>
      )}

      <div className="assistant-actions" aria-label="Suggested questions">
        {SUGGESTIONS.map((suggestion) => (
          <button
            key={suggestion}
            type="button"
            disabled={isStreaming}
            onClick={() => handleSendMessage(suggestion)}
          >
            {suggestion}
          </button>
        ))}
      </div>

      <form onSubmit={handleSubmit} className="assistant-input-row" aria-label="Ask assistant">
        <input
          type="text"
          value={input}
          onChange={(e) => setInput(e.target.value)}
          placeholder="Ask a question about Rafael's verified work (max 500 chars)..."
          maxLength={500}
          disabled={isStreaming}
          aria-label="Your question"
        />
        <div className="input-meta">
          <span className="char-count">{input.length}/500</span>
          <button type="submit" disabled={isStreaming || !input.trim()} className="button primary">
            Ask <span>⌁</span>
          </button>
        </div>
      </form>
    </div>
  );
}
