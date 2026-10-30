// Provider adapter only. The Contact backend owns consent, Turnstile and visitor limits.
// Never log payloads, addresses, authorization headers or provider exceptions.
const MAX_BYTES = 65536;
const SUBJECT = "Portfolio contact message";
const encoder = new TextEncoder();
const reply = (status, code) => Response.json({ error: code }, {
  status, headers: { "Cache-Control": "no-store" },
});

function mailbox(value) {
  return typeof value === "string" && value.length <= 254 &&
    /^[!-~]+@[a-zA-Z0-9.-]+\.[a-zA-Z0-9-]+$/.test(value) &&
    !/[\s,;<>]/.test(value) && value.split("@").length === 2;
}

function authenticated(request, expected) {
  if (typeof expected !== "string" || !/^[a-f0-9]{64}$/.test(expected)) return false;
  const presented = request.headers.get("Authorization");
  if (presented === null || presented.length !== 71) return false;
  const wanted = `Bearer ${expected}`;
  let difference = 0;
  for (let i = 0; i < wanted.length; i++) difference |= wanted.charCodeAt(i) ^ presented.charCodeAt(i);
  return difference === 0;
}

async function boundedBody(request) {
  if (Number(request.headers.get("Content-Length")) > MAX_BYTES) return null;
  const reader = request.body?.getReader();
  if (!reader) return null;
  const pieces = [];
  let size = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > MAX_BYTES) { await reader.cancel(); return null; }
      pieces.push(value);
    }
    const bytes = new Uint8Array(size);
    let offset = 0;
    for (const piece of pieces) { bytes.set(piece, offset); offset += piece.byteLength; }
    return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  } finally { reader.releaseLock(); }
}

function mime(payload) {
  // Visitor text lives only in a base64-encoded UTF-8 body, never raw MIME headers.
  const encoded = btoa(String.fromCharCode(...encoder.encode(payload.text)));
  return [
    `From: ${payload.from}`, `To: ${payload.to}`, `Reply-To: ${payload.reply_to}`,
    `Subject: ${SUBJECT}`, "MIME-Version: 1.0", 'Content-Type: text/plain; charset="utf-8"',
    "Content-Transfer-Encoding: base64", "", encoded.match(/.{1,76}/g).join("\r\n"), "",
  ].join("\r\n");
}

export async function relay(request, env, createMessage) {
  const url = new URL(request.url);
  if (!["dev", "prod"].includes(env.PORTFOLIO_STAGE) || env.CONTACT_RELAY_ENABLED !== "true")
    return reply(503, "unavailable");
  if (url.protocol !== "https:" || url.pathname !== `/v1/contact-relay/${env.PORTFOLIO_STAGE}` || url.search)
    return reply(404, "not_found");
  if (request.method !== "POST") return reply(405, "method_not_allowed");
  if (!authenticated(request, env.RELAY_TOKEN)) return reply(403, "forbidden");
  if (!mailbox(env.CONTACT_SENDER) || !mailbox(env.CONTACT_RECIPIENT) ||
      !env.EMAIL?.send || !env.RELAY_RATE_LIMIT?.limit) return reply(503, "unavailable");
  // Approximate per-location backstop, not a global invoice or delivery guarantee.
  try {
    if (!(await env.RELAY_RATE_LIMIT.limit({ key: env.PORTFOLIO_STAGE })).success)
      return reply(429, "provider_rate_limited");
  } catch { return reply(503, "unavailable"); }
  if (request.headers.get("Content-Type")?.split(";")[0].trim() !== "application/json" ||
      request.headers.has("Content-Encoding")) return reply(415, "unsupported_content");
  let payload;
  try {
    const body = await boundedBody(request);
    if (body === null) return reply(413, "too_large");
    payload = JSON.parse(body);
  } catch { return reply(400, "invalid_payload"); }
  if (!payload || typeof payload !== "object" || Array.isArray(payload) ||
      Object.keys(payload).sort().join(",") !== "from,reply_to,subject,text,to" ||
      payload.from !== env.CONTACT_SENDER || payload.to !== env.CONTACT_RECIPIENT ||
      payload.subject !== SUBJECT || !mailbox(payload.reply_to) ||
      typeof payload.text !== "string" || payload.text.length < 10 || payload.text.length > 5500)
    return reply(400, "invalid_payload");
  try {
    // Exactly one send; timeout/transport ambiguity must never trigger a second attempt.
    if (request.signal.aborted) return reply(499, "canceled");
    await env.EMAIL.send(createMessage(env.CONTACT_SENDER, env.CONTACT_RECIPIENT, mime(payload)));
    // Binding acceptance is queued, not proof that a human mailbox received the message.
    return Response.json({ success: true, result: { queued: [env.CONTACT_RECIPIENT] } }, {
      headers: { "Cache-Control": "no-store" },
    });
  } catch { return reply(502, "provider_error"); }
}
