# ADR 0003: Verified-destination email provider Worker

- Status: Accepted
- Date: 2026-10-29
- Extends: ADR 0001's independent-deployable rule

## Context

Contact sends to one owner-approved mailbox. The initial backend REST integration
requires Email Sending entitlement, while Cloudflare documents free sending to
verified destinations through Email Routing on the Workers Free plan. The owner
chose to minimize recurring infrastructure cost. The frontend cannot own contact
business policy or directly integrate with a cloud email provider.

## Decision

Add an independently released provider adapter Worker in `services/contact-email/`.
The .NET Contact module remains the owner of input validation, affirmative consent,
Turnstile, signed visitor admission, rate limiting and its existing public API.
Only that backend calls the authenticated Worker over HTTPS, using a distinct
32-byte random capability stored out of band. The Worker cannot choose recipients
from visitor input: configured sender/recipient must exactly match the request.
Both are secrets and the recipient must be verified in the Cloudflare account.

This is a transport adapter, not an extraction of business policy. It has its own
Workers runtime and release lifecycle, satisfying the condition in ADR 0001 for
the first real `services/` deployable. It does not belong in the Next.js app.
No durable storage, queue, transcript, model, mailbox client or Gmail grant is
introduced. The REST adapter remains available for explicitly selected accounts;
the new mode is opt-in and does not change existing deployments.

## Consequences

- No Email Sending subscription is required for the verified-destination route;
  Worker invocations remain subject to the chosen plan's quotas/pricing. This is
  not a promise that all infrastructure or all future usage is free.
- Backend and Worker credentials/configuration must agree. Distinct dev/prod
  Worker names, paths and capabilities prevent accidental stage crossover.
- The Worker exposes no visitor API and requires the capability for every send.
  TLS and secret handling protect this bearer credential; it is not a one-time
  token or a global exactly-once delivery guarantee. Never retry ambiguous sends.
- Worker acceptance is reported as queued, not delivered. Provider exceptions
  remain opaque. Messages exist only in request/provider memory, then the mailbox.
- There is one additional bounded HTTP hop. The existing backend deadline,
  finite Worker backstop and independent backend admission remain in force.
- The default Worker and backend remain off. Cloud resource/secret changes,
  synthetic sends, mailbox cleanup and production rollout require reviewed owner
  authorization. Existing empty REST secret containers are retained for rollback.

## Sources checked

- [Cloudflare pricing](https://developers.cloudflare.com/email-service/platform/pricing/)
- [Email send bindings](https://developers.cloudflare.com/email-service/configuration/send-bindings/)
- [Workers EmailMessage API](https://developers.cloudflare.com/email-service/api/send-emails/workers-api/#legacy-emailmessage-api)
