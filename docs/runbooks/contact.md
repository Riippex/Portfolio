# Contact relay: contract, configuration, and activation

The Contact module relays a visitor's message to one fixed inbox through the
Cloudflare Email Service REST API. It is a relay, not a mailbox: the application
stores nothing. This document is the public contract between the producer (the
.NET backend) and its consumers (the Next.js proxy and form), the configuration
reference, and the checklist for a later, owner-authorized activation.

**Status:** implemented and tested against a fake provider only. Real sending is
disabled by default and has never been run against Cloudflare. Nothing here
claims live delivery.

All values below are placeholders. Real addresses, account identifiers, tokens,
and sender and recipient choices live in private owner context and server
configuration, never in the repository.

## Data and retention

| Where | What happens |
| --- | --- |
| Browser | The draft lives in component memory only; nothing is written to browser storage. |
| Next.js proxy | Forwards the request body for one request; it keeps no copy. |
| Backend application | Validates, relays, and discards. No database, cache, queue, receipt, or draft. Logs hold a fixed outcome code, status, latency, and a pseudonymous client hash, never contact fields, the Turnstile token, provider payloads, or exception text. |
| Cloudflare Email Service | Processes the message and applies its own retention and logging. The application does not control it. |
| Recipient mailbox | The message is kept until the owner deletes it. |

"Zero application persistence" describes the application only. Provider
processing and mailbox retention are external, are not zero, and are not
automatically purged. Before launch the owner defines and verifies a mailbox
cleanup process and reviews the provider's retention terms. Selecting a
retention target does not implement it.

## Public contract

### Producer: `POST /v1/contact` (backend)

Request body, JSON, at most **64 KiB measured in bytes** (enforced while reading,
so it holds without a `Content-Length` header and for multibyte UTF-8):

| Field | Rule |
| --- | --- |
| `name` | Required, trimmed, 1 to 100 characters, no CR or LF. |
| `email` | Required, one valid mailbox, at most 254 characters, no CR, LF, comma, semicolon, or angle brackets. |
| `message` | Required, trimmed, 10 to 5,000 characters. |
| `consent` | Must be `true`. |
| `turnstileToken` | Human-verification token, required outside Development and Test. |

The visitor cannot choose the recipient, sender, CC or BCC, subject, headers, or
any provider URL. The outgoing message uses the configured sender and recipient,
the static subject `Portfolio contact message`, plain text only, and the
visitor's email as `reply_to`.

The backend also requires the signed visitor identity (`X-Portfolio-Identity` and
`X-Portfolio-Identity-Proof`, bound to the stage and to `POST /v1/contact`) outside
Development and Test, applies a separate Contact
rate limit (3 attempts per 10 minutes per identity by default), and verifies
Turnstile before any delivery.

Responses:

| HTTP | Body | Meaning |
| --- | --- | --- |
| 200 | `{"status":"delivered","outcome":"delivered"}` | The provider reported delivery to the configured recipient. |
| 200 | `{"status":"queued","outcome":"queued"}` | The provider accepted the message for delivery. This is not confirmed receipt. |
| 400 | `{"error": "..."}` | Missing, empty, malformed, or invalid input. |
| 403 | `{"error": "..."}` | Missing or invalid signed identity, or failed human verification. |
| 413 | `{"error": "..."}` | Body larger than 64 KiB. |
| 429 | `{"error": "..."}` and `Retry-After` | Contact rate limit reached. |
| 502 | `{"error": "...", "outcome": "<code>"}` | The relay failed or its outcome was unconfirmed. |
| 503 | `{"error": "...", "outcome": "unavailable"}` | Contact is disabled or not configured. |

The two 200 payloads above are the only success responses. `outcome` equals
`status` for both.

Failure `outcome` codes on 502: `provider_auth_error`, `provider_rate_limited`,
`provider_rejected` (HTTP 4xx or an unsuccessful envelope), `provider_error`
(HTTP 5xx), `provider_response_too_large`, `malformed_provider_response`,
`empty_provider_result`, `delivery_unconfirmed`, `permanent_bounce`, `timeout`,
`canceled`, `transport_error`, and `internal_error`. Codes are for diagnostics
and display logic; messages never include provider payloads.

### Consumer: `POST /api/contact` (Next.js proxy) and the form

- The proxy enforces the same 64 KiB limit before buffering, signs the visitor
  identity, forwards the body, and relays the backend status and `Retry-After`.
- The client treats a response as success **only** when the HTTP status is 2xx
  and the body is exactly one of the two documented success payloads. An HTTP 200
  with any other status, a missing field, or a mismatched `outcome` is shown as a
  failure.
- One submission is one request. There is no automatic retry. After a timeout or
  other ambiguous outcome the message may already have been delivered, so only
  the visitor can decide to send again. Each attempt obtains a fresh
  human-verification token.

### Provider adapter behavior

- Endpoint: `https://api.cloudflare.com/client/v4/accounts/<account-id>/email/sending/send`
  with a bearer token, from server configuration only.
- A delivery requires HTTP success **and** a success envelope
  (`success: true`) listing the configured recipient under `delivered` or
  `queued`. A non-2xx status never becomes a delivery, whatever the body says.
- The response body is read as a stream and abandoned beyond 16 KiB, with or
  without `Content-Length`.
- One attempt under one 15 second deadline that covers the request, the response
  headers, and the body. There is no retry and no durable idempotency record.
- Caller cancellation is reported as `canceled`, an elapsed deadline as `timeout`.

## Configuration

Set through server configuration or environment variables (double underscore
form). Defaults keep Contact off.

| Key | Environment variable | Placeholder | Notes |
| --- | --- | --- | --- |
| `Contact:Enabled` | `Contact__Enabled` | `false` | Default `false`. When `false`, `POST /v1/contact` returns 503. |
| `Contact:AccountId` | `Contact__AccountId` | `<cloudflare-account-id>` | Required when enabled. |
| `Contact:ApiToken` | `Contact__ApiToken` | `<scoped-api-token>` | Required when enabled. Secret: secret store only. |
| `Contact:SenderEmail` | `Contact__SenderEmail` | `noreply@<sender-domain>` | Required when enabled. Must belong to a verified sender domain. |
| `Contact:RecipientEmail` | `Contact__RecipientEmail` | `<owner-inbox>@<mail-domain>` | Required when enabled. Must be a verified destination. |
| `Turnstile:SecretKey` | `Turnstile__SecretKey` | `<turnstile-secret>` | Required outside Development and Test. |
| `AssistantSecurity:ProxyIdentitySecret` | `AssistantSecurity__ProxyIdentitySecret` | `<shared-proxy-secret>` | Required outside Development and Test; must equal the frontend secret. |
| `Portfolio:Stage` | `Portfolio__Stage` | `dev` or `prod` | Required outside Development and Test (Terraform sets it); an unset or unknown value stops the host. `local` is accepted only in Development and Test. |

Frontend (server-side unless noted): `ASSISTANT_PROXY_IDENTITY_SECRET`
(`<shared-proxy-secret>`), `TEAM_ALLOWLIST` (a Worker secret: a JSON array of at most
64 exact IPs, separate per environment), `PORTFOLIO_STAGE` (`dev` or `prod`, written by
the build), `BACKEND_API_URL` (`<backend-base-url>`), and
`NEXT_PUBLIC_TURNSTILE_SITE_KEY` (`<turnstile-site-key>`, public by design).

With `Contact:Enabled=true` the host refuses to start unless the account, token,
sender, and recipient are present and both addresses are valid mailboxes. Never
commit values; use secret stores and local, ignored configuration.

## Testing without sending

Automated tests inject a fake provider transport behind the real relay and drive
the real endpoint over a loopback listener. No test, and no default local run,
contacts Cloudflare or sends email, and there is no simulation or bypass flag in
production code. The published smoke test checks that Contact is disabled by
default and that unsigned requests are refused.

## Activation checklist (deferred; owner and Codex console work)

Do not start any of this as part of a code change. Each step needs the owner's
explicit authorization, and the provider documentation should be rechecked at
that time because the service is documented as beta.

1. Confirm the account is entitled to Cloudflare Email Service and review its
   pricing, limits, and retention terms.
2. Set up a Cloudflare-managed sender domain (`<sender-domain>`), reviewing the
   DNS impact and the permissions needed before changing records.
3. Add and verify the destination address (`<owner-inbox>@<mail-domain>`).
4. Create an API token limited to sending for this account; store it only in the
   secret store.
5. Configure the Turnstile secret and the shared proxy identity secret in both
   deployments.
6. Define and verify the mailbox cleanup process for received contact messages.
7. Authorize and perform one live smoke send, then record the observed result
   (delivered or queued, and what arrived) separately from the passing automated
   tests.
8. Set `Contact__Enabled=true`. To roll back, set it to `false`; the endpoint
   then returns 503 without touching the provider.

Until step 7 is recorded, delivery is unverified.
