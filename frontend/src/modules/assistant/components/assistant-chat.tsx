"use client";

import { useState, useRef, useEffect, useTransition } from "react";
import type { AssistantChatMessage, AssistantCitation, AssistantGroundingStatus } from "../model";
import { MODEL_PROCESSING_NOTICE_VERSION } from "../model";
import { streamAssistantChat } from "../api";
import { prepareAssistantChatRequest } from "../turnstile";
import { privacyContactEmail } from "../privacy-contact";
import Link from "next/link";

const INITIAL_MESSAGES: readonly AssistantChatMessage[] = [
  {
    id: "welcome",
    role: "assistant",
    content:
      "I am R AI, an interactive portfolio assistant grounded in Rafael's verified public evidence. Ask about Rafael's systems, focus areas, or architecture patterns.",
  },
];

const SUGGESTIONS = [
  "What is Vextis?",
  "Explain Rafael's focus areas",
  "What autonomous agent experience is documented?",
];

export function AssistantChat() {
  const privacyEmail = privacyContactEmail(process.env.NEXT_PUBLIC_PRIVACY_CONTACT_EMAIL);
  const [messages, setMessages] = useState<readonly AssistantChatMessage[]>(INITIAL_MESSAGES);
  const [input, setInput] = useState("");
  const [isStreaming, setIsStreaming] = useState(false);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [allowModelProcessing, setAllowModelProcessing] = useState(false);
  const [, startTransition] = useTransition();

  const terminalRef = useRef<HTMLDivElement>(null);
  const abortControllerRef = useRef<AbortController | null>(null);
  const messageCounterRef = useRef(0);

  useEffect(() => {
    if (terminalRef.current) {
      terminalRef.current.scrollTop = terminalRef.current.scrollHeight;
    }
  }, [messages]);

  const handleSendMessage = async (textToSend: string) => {
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

    let preparation;
    try {
      preparation = await prepareAssistantChatRequest(trimmed, undefined,
        allowModelProcessing && privacyEmail ? { accepted: true, noticeVersion: MODEL_PROCESSING_NOTICE_VERSION } : undefined);
    } catch {
      preparation = { kind: "verification_failed" as const };
    }

    if (preparation.kind === "verification_failed") {
      setIsStreaming(false);
      setErrorMessage("Human verification could not be completed. Please try again.");
      startTransition(() => {
        setMessages((prev) => prev.filter((msg) => msg.id !== assistantMessageId));
      });
      return;
    }

    streamAssistantChat(
      preparation.request,
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
            <span className="label">{msg.role === "user" ? "You" : "R AI"}</span>
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

      <div className="assistant-model-consent">
        <p id="assistant-model-notice">
          Optional Google AI: your current question, selected public portfolio facts and opaque
          accounting metadata may be sent to Google Cloud to help select relevant evidence.
          Processing is global, with no fixed-country guarantee, under Google&apos;s standard retention
          policies, including applicable abuse monitoring and caching. This is not zero retention.
          Leave this off to receive an evidence-based response without the external model;
          normal site security checks still apply.
        </p>
        <label className="assistant-model-choice">
          <input
            type="checkbox"
            checked={allowModelProcessing && !!privacyEmail}
            onChange={(e) => setAllowModelProcessing(e.target.checked)}
            disabled={isStreaming || !privacyEmail}
            aria-describedby="assistant-model-notice"
          />
          <span>I allow Google AI processing as described above for my questions in this open chat.</span>
        </label>
        {!privacyEmail && (
          <p role="status">The privacy contact is not configured. Optional Google AI is unavailable; you can still ask without the external model.</p>
        )}
        <details>
          <summary>Privacy details and changing your choice</summary>
          <p>
            Rafael Patiño operates this portfolio. For privacy questions or rights requests,
            {privacyEmail ? <> email <a href={`mailto:${privacyEmail}`}>{privacyEmail}</a>.</> : <> the privacy email is currently unavailable.</>}
            {" "}This channel is independent of the website Contact form. Do not send sensitive information or
            someone else&apos;s personal data. R AI sends only the current turn, not your chat history;
            the application does not save a transcript or this choice. Reloading resets
            this choice. Uncheck it before your next question to stop future model
            processing; this cannot recall data already processed by Google. The model may be
            unavailable even when you opt in. This permission is separate from Contact consent.
          </p>
          <p>
            Read <a href="https://docs.cloud.google.com/gemini-enterprise-agent-platform/resources/zero-data-retention" target="_blank" rel="noopener noreferrer">Google&apos;s processing and retention information</a>.
            Notice version: {MODEL_PROCESSING_NOTICE_VERSION}.
          </p>
        </details>
      </div>

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
          placeholder="Ask about Rafael's documented systems and evidence (max 500 chars)..."
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
