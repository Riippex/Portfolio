# Assistant chat and model-processing choice

Both `POST /v1/assistant/chat` and `POST /v1/assistant/chat/stream` accept the
same bounded request. JSON returns the existing grounded response; SSE retains
its existing status/citation/token/done contract. Identity, stage admission,
rate limits, Turnstile and safety checks apply regardless of the visitor's choice.

## Request

```json
{
  "message": "What does Rafael's profile document?",
  "modelConsent": {
    "accepted": true,
    "noticeVersion": "rai-google-global-standard-v1"
  }
}
```

- `message`: required, nonblank, at most 500 characters.
- `slug`: optional existing evidence filter, at most 64 alphanumeric/hyphen characters.
- `turnstileToken`: optional in local Development/Test only; remote stages still
  require the existing human-verification flow.
- `modelConsent`: optional. Only an object with exactly `accepted: true` and
  the case-sensitive current `noticeVersion` grants optional model processing.
  It is a request-scoped visitor assertion, not proof of identity, a durable
  consent receipt, or permission to bypass any other control.

Current notice: `rai-google-global-standard-v1`. It describes sending the current
question, selected public facts and opaque accounting metadata to Google Cloud
for relevant evidence selection, global processing, standard external retention
(not zero retention), responsible operator/contact, sensitive-data warning,
no application transcript/consent storage, and withdrawal for future submissions.
The frontend displays this notice before the initially unchecked choice and
links Google's retention information. Contact consent is separate.

The privacy channel is a direct `mailto:` link to the owner's approved public
alias, independent of the website Contact relay. The build receives it through
`NEXT_PUBLIC_PRIVACY_CONTACT_EMAIL`; never set the private forwarding inbox.
Missing or malformed configuration displays an unavailable notice, disables the
model checkbox and omits model consent from submissions; deterministic chat
remains available. This UI readiness check is not backend authorization or a
substitute for the provider activation review. No email is sent automatically.
The notice version stays unchanged: this corrects the contact channel without
changing provider, purpose, location, data scope or retention.

Missing/null, false, stale, incorrectly typed, duplicate/unknown inner properties,
or otherwise malformed optional consent is denied, not guessed/coerced. An
otherwise valid request still gets the deterministic evidence-based path. An
invalid overall JSON body or invalid message/filter remains an ordinary 400;
it never authorizes external processing. Old clients that omit consent work
without the model. Adding a consent header or using an allowlisted IP does not
grant model processing.

## Enforcement and lifecycle

With default configuration, the Assistant application service checks the choice before entering orchestration
in both async chat and SSE. This precedes credentials, ledger reservations,
CountTokens and generation. Counting is also external processing. Synchronous
`Chat` remains deterministic even with valid consent. An accepted choice is
necessary but not sufficient: reviewed provider configuration, safety, evidence,
budgets and availability must also permit the turn.

**Proposed operator opt-in, off by default:** [ADR 0004](../adr/0004-opt-in-control-client-startup-preparation.md)
describes dormant `Assistant:ModelControl:PrepareClientOnStart` scaffolding. If
separately adopted and explicitly enabled for a fully reviewed model/ledger pair,
startup may authenticate to the control store and read its one operational root
before any visitor consent. This is an explicit exception to the default
credentials lifecycle, not authorization for visitor data processing. It invokes
no Vertex credential path, counting, generation, reservation or write and retains
no root snapshot or cached allowance. Request model processing still requires the
same affirmative choice and fresh admission. The proposal is not accepted and no
environment activation is implied; leave the option unset pending owner review.

The checkbox is optional for free text and suggested questions. Its value lives
only in component/request memory, resets on reload, and can be unchecked between
submissions. Subsequent requests no longer authorize the model; this does not
recall processing already dispatched. The choice, notice version and visitor
identity are not added to logs, browser storage or the model-control ledger.
The choice itself is not provider input. No new persistent consent register is
approved. If one is required, define its lifecycle separately before release.

Change the notice version in the backend, frontend and this contract together
when provider, purpose, location, data scope or retention disclosure changes.
Older consent then fails closed; it cannot silently authorize the new notice.
Automated parity and bypass tests protect this boundary. These product controls
do not certify legal compliance or complete the actual provider/project review.
Provider activation and production release remain separately gated by the
[Vertex activation runbook](../runbooks/vertex-provider.md).
