# Contact email provider Worker

An isolated backend-to-provider adapter for Cloudflare's verified-destination
email route. See [ADR 0003](../../docs/adr/0003-verified-destination-email-worker.md)
and the [Contact runbook](../../docs/runbooks/contact.md). It is disabled by
default. The public Next.js frontend never calls or owns this provider adapter.

Run offline tests from the repository root:

```sh
node --test services/contact-email/relay.test.mjs
```

Use the existing locked frontend toolchain only as a build tool:

```sh
cd frontend
npx wrangler deploy --config ../services/contact-email/wrangler.jsonc --env dev --dry-run
```

No provider call or mail is sent by these commands. Unit tests inject a fake email
binding; Wrangler dry runs check packaging, not account entitlement or delivery.

## Private transport contract

Only `POST /v1/contact-relay/{stage}` over HTTPS, without a query string, is
accepted. The exact `dev` or `prod` stage must match the Worker configuration.
Authorization is `Bearer <64 lowercase hexadecimal characters>`, generated from
32 random bytes, distinct per stage and from all visitor/Turnstile/model secrets.
There is no credential in a URL or public client. Store it as Worker `RELAY_TOKEN`
and in the backend's existing stage-owned Contact API-token secret container;
in Worker mode that container holds a relay capability, not a Cloudflare API token.

The JSON body uses the existing provider wire shape: exactly `from`, `to`,
`reply_to`, `subject`, `text`. Sender and recipient must equal Worker secrets
`CONTACT_SENDER` and `CONTACT_RECIPIENT`; subject is fixed as
`Portfolio contact message`. No CC/BCC, HTML, attachments, arbitrary headers,
recipient selection or generic URL fetch. UTF-8 body is bounded at 64 KiB even
without Content-Length. Text is bounded at 5,500 UTF-16 code units including the
backend's name/email prefix. Header mailboxes are conservatively ASCII-only;
unsupported provider header syntax fails closed, never coerces a recipient.

The Worker creates a plain-text MIME message with base64-encoded UTF-8 body and
calls its Email Routing `send_email` binding once. A successful binding invocation
returns `{"success":true,"result":{"queued":["<configured-recipient>"]}}`.
This is acceptance, not proof of inbox receipt. Failures return bounded fixed
codes with HTTP 4xx/5xx. Provider details, credentials, recipient and message are
never returned in an unauthenticated response or written to logs.

An authenticated, validated request whose email binding fails still returns
exactly HTTP 502 and `{"error":"provider_error"}`. Only that protected failure
response adds `X-Portfolio-Relay-Outcome`: one fixed category from
`sender_unverified`, `recipient_not_allowed`, `sender_unavailable`, `rate_limited`,
`delivery_failed`, `provider_unavailable`, or `unknown`. These categories map only
exact documented Cloudflare `error.code` values; no exception message, arbitrary
code, address or provider body is reflected. Legacy errors without a recognized
code remain `unknown`. Pre-send rejections and successful replies have no such
header. The .NET adapter ignores it, preserving the public Contact API. This is
bounded internal transport metadata, not a delivery receipt or permission to
retry. See Cloudflare's [error codes](https://developers.cloudflare.com/email-service/api/send-emails/workers-api/#error-codes).

All responses use `Cache-Control: no-store`. No mailbox access or application
persistence. The binding with only a name is restricted by Cloudflare to verified
destinations in the account; the adapter further fixes one configured recipient.
The rate-limit binding admits six authenticated attempts per minute per location,
shared within the stage. It is approximate and is not a global cost cap. The
backend's visitor consent, Turnstile and separate limits remain authoritative.

## Operator release and activation

The dedicated manual `Deploy Contact relay` workflow validates the exact source,
requires develop/dev or main/prod pairing, and uses the existing owner environment
approval gate. It deploys only this Worker, with sends off by default. No account
plan, DNS record, token or secret value is changed by source validation.

The workflow reads only the stage environment secret `CLOUDFLARE_CONTACT_API_TOKEN`,
separate from the frontend's credential. Bootstrap the disabled Worker once with
the owner's explicitly authorized session: per-Worker Editor cannot create a
resource. Then provision an expiring Individual Workers Editor token restricted
to this exact Worker for ongoing CI deployments. No account-wide CI Admin or
frontend token expansion is required. Token creation and storage in the matching
GitHub environment need explicit owner authorization; do not publish values.

After reviewed owner authorization, deploy disabled, verify the recipient in
Email Routing, and set the three Worker secrets out of band. The sending domain
must meet Cloudflare's verified-domain requirements; inspect exact DNS impact
before onboarding or changing records. Add matching backend secret versions out
of band. The account-id container is unused in Worker mode; do not delete it.

For an explicitly authorized internal smoke, dispatch with `enable_relay=true`;
the workflow checks secret names, and the runtime validates actual values. This
does not enable the public Contact form. Do not send until the owner authorizes
the exact recipient and synthetic payload. No automatic retry.

Backend Terraform `contact.relay_mode="worker"` and the exact stage URL select
this mode. Full application activation still requires provider, mailbox cleanup
and observed delivery gates. Never set them merely to make a plan pass. Review
and approve the full saved plan before applying it. Re-test the form after
activation; fake tests and queued replies are insufficient proof of receipt.

Rollback: deploy this Worker with `enable_relay=false` and/or set backend Contact
enabled false through its reviewed plan. Retain secret containers and accounting.
No Gmail cleanup operation, subscription, permanent deletion or prod promotion
is part of this source change.
