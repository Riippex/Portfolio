import assert from "node:assert/strict";
import { test } from "node:test";
import { readFileSync } from "node:fs";
import { relay } from "./relay.mjs";

const token = "a".repeat(64); // Synthetic fixture, never an installed credential.
const address = "https://rafael-portfolio-contact-dev.example.workers.dev/v1/contact-relay/dev";
const payload = { from: "contact@example.com", to: "owner@example.net", reply_to: "visitor@example.org", subject: "Portfolio contact message", text: "From: Visitor\n\nMessage:\nHello about your portfolio." };
function fixture(overrides = {}) {
  const sent = [];
  const env = {
    PORTFOLIO_STAGE: "dev", CONTACT_RELAY_ENABLED: "true", RELAY_TOKEN: token,
    CONTACT_SENDER: payload.from, CONTACT_RECIPIENT: payload.to,
    RELAY_RATE_LIMIT: { limit: async () => ({ success: true }) },
    EMAIL: { send: async (message) => { sent.push(message); } }, ...overrides,
  };
  return { env, sent };
}
function request(body = payload, options = {}) {
  return new Request(options.url ?? address, {
    method: options.method ?? "POST",
    headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json", ...options.headers },
    ...(options.method === "GET" ? {} : { body: typeof body === "string" ? body : JSON.stringify(body) }),
  });
}
const createMessage = (from, to, raw) => ({ from, to, raw });
test("one accepted message preserves MIME isolation and reports queued only", async () => {
  const { env, sent } = fixture();
  const body = { ...payload, text: "Hello café\r\nBcc: injected@example.org" };
  const response = await relay(request(body), env, createMessage);
  assert.equal(response.status, 200);
  assert.equal(response.headers.get("X-Portfolio-Relay-Outcome"), null);
  assert.equal(response.headers.get("Cache-Control"), "no-store");
  assert.deepEqual(await response.json(), { success: true, result: { queued: [payload.to] } });
  assert.equal(sent.length, 1);
  assert.equal(sent[0].from, payload.from);
  assert.equal(sent[0].to, payload.to);
  const [headers, encoded] = sent[0].raw.split("\r\n\r\n");
  assert.ok(!headers.includes("Bcc:"));
  assert.equal(Buffer.from(encoded.replaceAll("\r\n", ""), "base64").toString("utf8"), body.text);
});

test("MIME has one current RFC 5322 Date that visitor text cannot override", async () => {
  const { env, sent } = fixture();
  const body = { ...payload, text: "Date: Fri, 01 Jan 1999 00:00:00 +0000\r\nA visitor supplied this old date." };
  const before = Math.floor(Date.now() / 1000) * 1000;
  const response = await relay(request(body), env, createMessage);
  const after = Date.now();
  assert.equal(response.status, 200);
  assert.equal(sent.length, 1);
  const [headers, encoded] = sent[0].raw.split("\r\n\r\n");
  const dates = headers.split("\r\n").filter((line) => /^Date:/i.test(line));
  assert.equal(dates.length, 1);
  const value = dates[0].slice("Date: ".length);
  assert.match(value, /^(Mon|Tue|Wed|Thu|Fri|Sat|Sun), \d{2} (Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) \d{4} \d{2}:\d{2}:\d{2} \+0000$/);
  const timestamp = Date.parse(value);
  assert.ok(Number.isFinite(timestamp));
  assert.ok(timestamp >= before && timestamp <= after);
  assert.equal(new Date(timestamp).toUTCString().replace(/GMT$/, "+0000"), value);
  assert.equal(Buffer.from(encoded.replaceAll("\r\n", ""), "base64").toString("utf8"), body.text);
});

for (const [name, overrides, modify, status] of [
  ["disabled", { CONTACT_RELAY_ENABLED: "false" }, {}, 503],
  ["missing stage", { PORTFOLIO_STAGE: undefined }, {}, 503],
  ["unknown stage", { PORTFOLIO_STAGE: "local" }, {}, 503],
  ["missing token", { RELAY_TOKEN: undefined }, {}, 403],
  ["weak token", { RELAY_TOKEN: "short" }, {}, 403],
  ["wrong token", {}, { headers: { Authorization: `Bearer ${"b".repeat(64)}` } }, 403],
  ["missing authorization", {}, { headers: { Authorization: "" } }, 403],
  ["cross-stage", { PORTFOLIO_STAGE: "prod" }, {}, 404],
  ["query string", {}, { url: address + "?recipient=other@example.com" }, 404],
  ["non-TLS", {}, { url: address.replace("https:", "http:") }, 404],
  ["public GET", {}, { method: "GET" }, 405],
  ["missing sender", { CONTACT_SENDER: undefined }, {}, 503],
  ["bad recipient", { CONTACT_RECIPIENT: "a@example.com\r\nBcc: b@example.com" }, {}, 503],
  ["missing email binding", { EMAIL: undefined }, {}, 503],
  ["missing limiter", { RELAY_RATE_LIMIT: undefined }, {}, 503],
  ["exhausted limiter", { RELAY_RATE_LIMIT: { limit: async () => ({ success: false }) } }, {}, 429],
  ["broken limiter", { RELAY_RATE_LIMIT: { limit: async () => { throw Error("sensitive"); } } }, {}, 503],
  ["unsupported body", {}, { headers: { "Content-Type": "text/plain" } }, 415],
  ["encoded body", {}, { headers: { "Content-Encoding": "gzip" } }, 415],
]) {
  test(`${name} never reaches the email binding`, async () => {
    const { env, sent } = fixture(overrides);
    const response = await relay(request(payload, modify), env, createMessage);
    assert.equal(response.status, status);
    assert.equal(response.headers.get("X-Portfolio-Relay-Outcome"), null);
    assert.equal(sent.length, 0);
    assert.ok(!(await response.text()).includes("sensitive"));
  });
}
for (const body of [null, [], "invalid JSON", { ...payload, to: "attacker@example.org" },
  { ...payload, from: "attacker@example.org" }, { ...payload, subject: "Visitor subject" },
  { ...payload, reply_to: "v@example.org\r\nBcc: attacker@example.org" },
  { ...payload, reply_to: "v@example.org,b@example.org" },
  { ...payload, text: "x".repeat(5501) }, { ...payload, text: "short" },
  { ...payload, cc: "attacker@example.org" }, { ...payload, html: "<script>" }]) {
  test("malformed or recipient-changing input is rejected before sending", async () => {
    const { env, sent } = fixture();
    assert.equal((await relay(request(body), env, createMessage)).status, 400);
    assert.equal(sent.length, 0);
  });
}
test("oversized body is bounded without trusting Content-Length", async () => {
  const { env, sent } = fixture();
  assert.equal((await relay(request("x".repeat(65537)), env, createMessage)).status, 413);
  assert.equal((await relay(request(payload, { headers: { "Content-Length": "999999" } }), env, createMessage)).status, 413);
  assert.equal(sent.length, 0);
});
test("provider exception is opaque and never retried", async () => {
  let calls = 0;
  const { env } = fixture({ EMAIL: { send: async () => { calls++; throw Error("private provider body"); } } });
  const response = await relay(request(), env, createMessage);
  assert.equal(response.status, 502);
  assert.equal(response.headers.get("X-Portfolio-Relay-Outcome"), "unknown");
  assert.ok(!(await response.text()).includes("private"));
  assert.equal(calls, 1);
});

for (const [code, outcome] of [
  ["E_SENDER_NOT_VERIFIED", "sender_unverified"],
  ["E_RECIPIENT_NOT_ALLOWED", "recipient_not_allowed"],
  ["E_SENDER_DOMAIN_NOT_AVAILABLE", "sender_unavailable"],
  ["E_RATE_LIMIT_EXCEEDED", "rate_limited"],
  ["E_DAILY_LIMIT_EXCEEDED", "rate_limited"],
  ["E_DELIVERY_FAILED", "delivery_failed"],
  ["E_INTERNAL_SERVER_ERROR", "provider_unavailable"],
]) {
  test(`documented ${code} exposes only a protected fixed category`, async () => {
    let calls = 0;
    const { env } = fixture({ EMAIL: { send: async () => {
      calls++;
      throw Object.assign(Error("private provider message owner@example.net token-value"), { code });
    } } });
    const response = await relay(request(), env, createMessage);
    assert.equal(response.status, 502);
    assert.equal(await response.text(), '{"error":"provider_error"}');
    assert.equal(response.headers.get("Cache-Control"), "no-store");
    assert.equal(response.headers.get("X-Portfolio-Relay-Outcome"), outcome);
    assert.equal(calls, 1);
  });
}

for (const error of [
  null, "private provider message", {},
  { code: "owner@example.net private-token" },
  { code: "E_DELIVERY_FAILED\r\nX-Private: owner@example.net" },
  { code: "e_delivery_failed" }, { code: new String("E_DELIVERY_FAILED") },
  { code: 123 }, { code: { toString() { throw Error("private value"); } } },
  { get code() { throw Error("private getter value"); } },
]) {
  test("unknown or forged provider codes cannot leak through headers or body", async () => {
    let calls = 0;
    const { env } = fixture({ EMAIL: { send: async () => { calls++; throw error; } } });
    const response = await relay(request(), env, createMessage);
    assert.equal(response.status, 502);
    assert.equal(await response.text(), '{"error":"provider_error"}');
    assert.equal(response.headers.get("X-Portfolio-Relay-Outcome"), "unknown");
    assert.equal(calls, 1);
  });
}
test("an aborted caller does not start a send", async () => {
  const { env, sent } = fixture();
  const controller = new AbortController();
  const source = request();
  const req = new Request(source, { signal: controller.signal });
  controller.abort();
  assert.equal((await relay(req, env, createMessage)).status, 499);
  assert.equal(sent.length, 0);
});
test("both deployment stages default off with distinct finite limit namespaces and no payload logs", () => {
  const config = JSON.parse(readFileSync(new URL("wrangler.jsonc", import.meta.url), "utf8"));
  assert.equal(config.observability.enabled, false);
  const ids = new Set();
  for (const stage of ["dev", "prod"]) {
    const env = config.env[stage];
    assert.equal(env.name, `rafael-portfolio-contact-${stage}`);
    assert.equal(env.vars.PORTFOLIO_STAGE, stage);
    assert.equal(env.vars.CONTACT_RELAY_ENABLED, "false");
    assert.deepEqual(env.send_email, [{ name: "EMAIL" }]);
    assert.equal(env.ratelimits[0].simple.limit, 6);
    assert.equal(env.ratelimits[0].simple.period, 60);
    ids.add(env.ratelimits[0].namespace_id);
    for (const secret of ["RELAY_TOKEN", "CONTACT_SENDER", "CONTACT_RECIPIENT"]) assert.ok(!(secret in env.vars));
  }
  assert.equal(ids.size, 2);
});
