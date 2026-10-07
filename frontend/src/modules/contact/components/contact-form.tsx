"use client";

import { useRef, useState } from "react";
import { acquireTurnstileToken } from "@/modules/assistant/turnstile";
import { sendContactMessage } from "../api";
import {
  MAX_NAME_LENGTH,
  MAX_EMAIL_LENGTH,
  MIN_MESSAGE_LENGTH,
  MAX_MESSAGE_LENGTH,
} from "../model";

type FormState = "idle" | "submitting" | "delivered" | "queued" | "unavailable" | "error";

export function ContactForm() {
  const [name, setName] = useState("");
  const [email, setEmail] = useState("");
  const [message, setMessage] = useState("");
  const [consent, setConsent] = useState(false);
  const [state, setState] = useState<FormState>("idle");
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  // State updates are asynchronous; the ref closes the window in which two submit events
  // could both observe an idle form and send the same message twice.
  const inFlight = useRef(false);

  const trimmedName = name.trim();
  const trimmedEmail = email.trim();
  const trimmedMessage = message.trim();

  const isNameValid = trimmedName.length >= 1 && trimmedName.length <= MAX_NAME_LENGTH;
  const isEmailValid = trimmedEmail.length >= 3 && trimmedEmail.length <= MAX_EMAIL_LENGTH && trimmedEmail.includes("@");
  const isMessageValid = trimmedMessage.length >= MIN_MESSAGE_LENGTH && trimmedMessage.length <= MAX_MESSAGE_LENGTH;
  const canSubmit = isNameValid && isEmailValid && isMessageValid && consent && state !== "submitting";

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!canSubmit || inFlight.current) return;

    inFlight.current = true;
    setState("submitting");
    setErrorMessage(null);

    try {
      // Acquire fresh Turnstile token for this submission attempt
      const tokenResult = await acquireTurnstileToken();
      if (tokenResult.kind === "failed") {
        setState("error");
        setErrorMessage("Human verification failed or expired. Please try again.");
        return;
      }

      const turnstileToken = tokenResult.kind === "token" ? tokenResult.token : undefined;

      // One attempt per user action: there is no automatic retry, because after an
      // ambiguous outcome the message may already have been delivered.
      const result = await sendContactMessage({
        name: trimmedName,
        email: trimmedEmail,
        message: trimmedMessage,
        consent,
        turnstileToken,
      });

      if (result.ok) {
        setState(result.data.status === "delivered" ? "delivered" : "queued");
      } else if (result.status === 503) {
        setState("unavailable");
        setErrorMessage(result.error);
      } else {
        setState("error");
        setErrorMessage(result.error);
      }
    } finally {
      inFlight.current = false;
    }
  }

  function handleReset() {
    setName("");
    setEmail("");
    setMessage("");
    setConsent(false);
    setState("idle");
    setErrorMessage(null);
  }

  if (state === "delivered" || state === "queued") {
    return (
      <div className="contact-status-card" role="status" aria-live="polite">
        <div className="contact-status-icon" aria-hidden="true">✓</div>
        <h3>{state === "delivered" ? "Message Delivered" : "Message Queued for Delivery"}</h3>
        <p>
          {state === "delivered"
            ? "Your message has been relayed to Rafael's inbox via Cloudflare Email Service."
            : "Your message has been accepted by Cloudflare Email Service and queued for delivery."}
        </p>
        <p className="contact-privacy-note">
          Zero application persistence: no drafts, logs, or database records of your message were saved.
        </p>
        <button
          type="button"
          className="button secondary"
          onClick={handleReset}
        >
          Send another message
        </button>
      </div>
    );
  }

  return (
    <div className="contact-card" aria-label="Contact form">
      <form onSubmit={handleSubmit} noValidate>
        {errorMessage && (
          <div className="contact-error-banner" role="alert" aria-live="assertive">
            <strong>{state === "unavailable" ? "Service Unavailable" : "Submission Failed"}</strong>
            <p>{errorMessage}</p>
          </div>
        )}

        <div className="contact-field-group">
          <div className="contact-field">
            <label htmlFor="contact-name" className="contact-label">
              Your Name <span className="contact-required">*</span>
            </label>
            <input
              id="contact-name"
              type="text"
              className="contact-input"
              placeholder="e.g. Jane Doe"
              maxLength={MAX_NAME_LENGTH}
              value={name}
              onChange={(e) => setName(e.target.value)}
              disabled={state === "submitting"}
              required
            />
          </div>

          <div className="contact-field">
            <label htmlFor="contact-email" className="contact-label">
              Reply Email <span className="contact-required">*</span>
            </label>
            <input
              id="contact-email"
              type="email"
              className="contact-input"
              placeholder="e.g. jane@example.com"
              maxLength={MAX_EMAIL_LENGTH}
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              disabled={state === "submitting"}
              required
            />
          </div>
        </div>

        <div className="contact-field">
          <div className="contact-label-row">
            <label htmlFor="contact-message" className="contact-label">
              Message <span className="contact-required">*</span>
            </label>
            <span
              className={`contact-counter ${
                trimmedMessage.length > MAX_MESSAGE_LENGTH
                  ? "over-limit"
                  : trimmedMessage.length < MIN_MESSAGE_LENGTH && trimmedMessage.length > 0
                  ? "under-limit"
                  : ""
              }`}
              aria-live="polite"
            >
              {message.length} / {MAX_MESSAGE_LENGTH} characters
            </span>
          </div>
          <textarea
            id="contact-message"
            className="contact-textarea"
            rows={6}
            placeholder="Write your message here (10 to 5,000 characters)..."
            value={message}
            onChange={(e) => setMessage(e.target.value)}
            disabled={state === "submitting"}
            required
          />
        </div>

        <div className="contact-consent">
          <label className="contact-checkbox-label">
            <input
              type="checkbox"
              id="contact-consent-checkbox"
              className="contact-checkbox"
              checked={consent}
              onChange={(e) => setConsent(e.target.checked)}
              disabled={state === "submitting"}
              required
            />
            <span>
              I consent to relaying my name, email address, and message directly to
              Rafael&apos;s personal inbox via Cloudflare Email Service. No database record or draft is retained.
            </span>
          </label>
        </div>

        <div className="contact-actions">
          <button
            type="submit"
            className="button primary contact-submit-button"
            disabled={!canSubmit}
            aria-busy={state === "submitting"}
          >
            {state === "submitting" ? "Sending..." : "Send Message"}
            <span aria-hidden="true"> ↗</span>
          </button>
        </div>
      </form>
    </div>
  );
}
