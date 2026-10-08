// Proves the generated-Worker checks by breaking a realistic generated configuration in memory and
// requiring the matching problem. A mutation that changes nothing fails loudly.
//
//   node --test tools/

import assert from "node:assert/strict";
import { test } from "node:test";
import { ADMISSION_MARKERS, checkGeneratedWorker } from "./check-wrangler-build.mjs";

const baseName = "rafael-portfolio-frontend";

function generatedFor(stage) {
  return {
    name: `${baseName}-${stage}`,
    targetEnvironment: stage,
    topLevelName: baseName,
    workers_dev: stage !== "prod",
    vars: { PORTFOLIO_STAGE: stage },
    assets: { directory: "../client", not_found_handling: "none", binding: "ASSETS", run_worker_first: true },
    ratelimits: [
      { name: "RATE_LIMIT_IP_ORDINARY", namespace_id: "1101", simple: { limit: 5, period: 60 } },
      { name: "RATE_LIMIT_IP_TEAM", namespace_id: "1102", simple: { limit: 15, period: 60 } },
      { name: "RATE_LIMIT_COUNTRY", namespace_id: "1103", simple: { limit: 100, period: 60 } },
    ],
  };
}

const bundle = `// worker\n${ADMISSION_MARKERS.map((marker) => `"${marker}"`).join(";\n")}\n`;

const check = (stage, mutate = (generated) => generated, bundleText = bundle) =>
  checkGeneratedWorker({ stage, baseName, generated: mutate(generatedFor(stage)), bundle: bundleText });

test("a correct dev and prod build has no problems", () => {
  assert.deepEqual(check("dev"), []);
  assert.deepEqual(check("prod"), []);
});

const mutations = [
  ["stage variable missing", (g) => ({ ...g, vars: {} }), /PORTFOLIO_STAGE=dev/],
  ["stage variable names another stage", (g) => ({ ...g, vars: { PORTFOLIO_STAGE: "prod" } }), /PORTFOLIO_STAGE=dev/],
  ["stage variable has a non-canonical value", (g) => ({ ...g, vars: { PORTFOLIO_STAGE: "development" } }), /PORTFOLIO_STAGE=dev/],
  ["extra variable baked in", (g) => ({ ...g, vars: { ...g.vars, PORTFOLIO_BACKEND_URL: "https://x" } }), /nothing else/],
  ["allowlist baked into the build", (g) => ({ ...g, vars: { ...g.vars, TEAM_ALLOWLIST: '["203.0.113.4"]' } }), /allowlist|nothing else/],
  ["secret baked into the build", (g) => ({ ...g, vars: { ...g.vars, ASSISTANT_PROXY_IDENTITY_SECRET: "x" } }), /secrets|nothing else/],
  ["Worker does not run before assets", (g) => ({ ...g, assets: { ...g.assets, run_worker_first: false } }), /run_worker_first/],
  ["run_worker_first omitted", (g) => ({ ...g, assets: { directory: "../client", binding: "ASSETS" } }), /run_worker_first/],
  ["assets binding missing", (g) => ({ ...g, assets: { run_worker_first: true } }), /ASSETS binding/],
  ["rate-limit bindings missing", (g) => ({ ...g, ratelimits: [] }), /rate-limit bindings must be exactly/],
  ["country binding missing", (g) => ({ ...g, ratelimits: g.ratelimits.slice(0, 2) }), /rate-limit bindings must be exactly/],
  ["unexpected extra binding", (g) => ({ ...g, ratelimits: [...g.ratelimits, { name: "OTHER", namespace_id: "9", simple: { limit: 1, period: 60 } }] }), /rate-limit bindings must be exactly/],
  ["ordinary limit raised", (g) => ({ ...g, ratelimits: g.ratelimits.map((r) => (r.name === "RATE_LIMIT_IP_ORDINARY" ? { ...r, simple: { limit: 50, period: 60 } } : r)) }), /RATE_LIMIT_IP_ORDINARY must allow 5/],
  ["team limit lowered", (g) => ({ ...g, ratelimits: g.ratelimits.map((r) => (r.name === "RATE_LIMIT_IP_TEAM" ? { ...r, simple: { limit: 3, period: 60 } } : r)) }), /RATE_LIMIT_IP_TEAM must allow 15/],
  ["country period changed", (g) => ({ ...g, ratelimits: g.ratelimits.map((r) => (r.name === "RATE_LIMIT_COUNTRY" ? { ...r, simple: { limit: 100, period: 10 } } : r)) }), /RATE_LIMIT_COUNTRY must allow 100 per 60s/],
  ["duplicate namespace ids", (g) => ({ ...g, ratelimits: g.ratelimits.map((r) => ({ ...r, namespace_id: "1101" })) }), /unique/],
  ["non numeric namespace id", (g) => ({ ...g, ratelimits: g.ratelimits.map((r) => ({ ...r, namespace_id: "abc" })) }), /numeric namespace_id/],
  ["Worker name drifts", (g) => ({ ...g, name: "frontend" }), /Worker name/],
  ["target environment drifts", (g) => ({ ...g, targetEnvironment: "prod" }), /target environment/],
];

for (const [name, mutate, expected] of mutations) {
  test(`rejects: ${name}`, () => {
    const before = JSON.stringify(generatedFor("dev"));
    assert.notEqual(JSON.stringify(mutate(generatedFor("dev"))), before, "the mutation did not change the configuration");
    const problems = check("dev", mutate);
    assert.ok(problems.some((problem) => expected.test(problem)), `expected ${expected}, got ${JSON.stringify(problems)}`);
  });
}

test("rejects a bundle without the admission entry", () => {
  const problems = check("dev", (g) => g, "export default { fetch() {} };");
  assert.ok(problems.some((problem) => /admission entry/.test(problem)));
});

test("rejects stages that are not deployed stages", () => {
  const problems = check("local");
  assert.ok(problems.some((problem) => /dev and prod/.test(problem)));
  assert.ok(check("staging").some((problem) => /dev and prod/.test(problem)));
});
