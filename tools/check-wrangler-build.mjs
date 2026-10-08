#!/usr/bin/env node
// Offline check of the Worker configuration Vinext generated for one stage.
//
//   CLOUDFLARE_ENV=<stage> npm run build:vinext   (in frontend/)
//   node tools/check-wrangler-build.mjs <stage>
//
// It reads frontend/dist/server only. It confirms that the stage's build fixes the Worker name
// Terraform routes to, selects the matching Wrangler environment, bakes in exactly the explicit
// PORTFOLIO_STAGE (and no secret), runs the Worker before static assets, declares the three
// edge rate-limit bindings with the approved limits, and bundles the admission entry.

import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

export const EDGE_LIMIT_BINDINGS = {
  RATE_LIMIT_IP_ORDINARY: 5,
  RATE_LIMIT_IP_TEAM: 15,
  RATE_LIMIT_COUNTRY: 100,
};
export const EDGE_LIMIT_PERIOD_SECONDS = 60;
export const ADMISSION_MARKERS = [
  "Private development access is not configured.",
  "Edge rate limiting is not configured.",
  "RATE_LIMIT_IP_ORDINARY",
];

// Returns the problems found in a generated Worker config and bundle for `stage`.
export function checkGeneratedWorker({ stage, baseName, generated, bundle }) {
  const problems = [];
  const expect = (condition, message) => {
    if (!condition) problems.push(message);
  };

  expect(baseName, "frontend/wrangler.jsonc must declare a base name");
  expect(generated.name === `${baseName}-${stage}`, `Worker name is ${generated.name}, expected ${baseName}-${stage}`);
  expect(generated.targetEnvironment === stage, `target environment is ${generated.targetEnvironment}, expected ${stage}`);
  expect(generated.topLevelName === baseName, `top-level name is ${generated.topLevelName}, expected ${baseName}`);
  expect(generated.workers_dev === (stage !== "prod"), `workers_dev is ${generated.workers_dev}; only non-prod stages use workers.dev`);

  const vars = generated.vars ?? {};
  expect(
    JSON.stringify(vars) === JSON.stringify({ PORTFOLIO_STAGE: stage }),
    `the build must bake exactly PORTFOLIO_STAGE=${stage} and nothing else; found ${JSON.stringify(Object.keys(vars))}`
  );
  expect(["dev", "prod"].includes(stage), `deployed stages are dev and prod, not ${JSON.stringify(stage)}`);
  expect(!/SECRET|TOKEN|PASSWORD|ALLOWLIST/i.test(JSON.stringify(vars)), "the build must not contain secrets or the allowlist");

  expect(generated.assets?.binding === "ASSETS", "the ASSETS binding must be present");
  expect(
    generated.assets?.run_worker_first === true,
    "assets.run_worker_first must be true so the Worker admits requests before static assets are served"
  );

  const limits = generated.ratelimits ?? [];
  const names = limits.map((limit) => limit.name).sort();
  expect(
    JSON.stringify(names) === JSON.stringify(Object.keys(EDGE_LIMIT_BINDINGS).sort()),
    `rate-limit bindings must be exactly ${Object.keys(EDGE_LIMIT_BINDINGS)}; found ${JSON.stringify(names)}`
  );
  for (const limit of limits) {
    expect(
      limit.simple?.limit === EDGE_LIMIT_BINDINGS[limit.name] && limit.simple?.period === EDGE_LIMIT_PERIOD_SECONDS,
      `${limit.name} must allow ${EDGE_LIMIT_BINDINGS[limit.name]} per ${EDGE_LIMIT_PERIOD_SECONDS}s; found ${JSON.stringify(limit.simple)}`
    );
    expect(/^\d+$/.test(String(limit.namespace_id)), `${limit.name} needs a numeric namespace_id`);
  }
  expect(
    new Set(limits.map((limit) => limit.namespace_id)).size === limits.length,
    "rate-limit namespace ids must be unique"
  );

  for (const marker of ADMISSION_MARKERS) {
    expect(bundle.includes(marker), `the Worker bundle does not contain the admission entry (missing ${JSON.stringify(marker)})`);
  }

  return problems;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const stage = process.argv[2];
  if (!stage) {
    console.error("usage: node tools/check-wrangler-build.mjs <stage>");
    process.exit(2);
  }

  const root = join(dirname(fileURLToPath(import.meta.url)), "..");
  const generated = JSON.parse(readFileSync(join(root, "frontend/dist/server/wrangler.json"), "utf8"));
  const source = readFileSync(join(root, "frontend/wrangler.jsonc"), "utf8");
  const baseName = /"name"\s*:\s*"([^"]+)"/.exec(source)?.[1];
  const bundle = readFileSync(join(root, "frontend/dist/server", generated.main ?? "index.js"), "utf8");

  const problems = checkGeneratedWorker({ stage, baseName, generated, bundle });
  if (problems.length > 0) {
    console.error(`Worker build for stage "${stage}" is wrong:`);
    for (const problem of problems) console.error(`  ${problem}`);
    process.exit(1);
  }
  console.log(`Worker build for stage "${stage}" is consistent (${generated.name}).`);
}
