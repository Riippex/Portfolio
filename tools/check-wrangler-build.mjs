#!/usr/bin/env node
// Offline check of the Worker configuration Vinext generated for one stage.
//
//   CLOUDFLARE_ENV=<stage> npm run build:vinext   (in frontend/)
//   node tools/check-wrangler-build.mjs <stage>
//
// It reads frontend/dist/server/wrangler.json only. It confirms that the stage's build
// fixes the Worker name Terraform routes to, selects the matching Wrangler environment,
// and bakes in no runtime variables or secrets.

import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const stage = process.argv[2];
if (!stage) {
  console.error("usage: node tools/check-wrangler-build.mjs <stage>");
  process.exit(2);
}

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const generated = JSON.parse(readFileSync(join(root, "frontend/dist/server/wrangler.json"), "utf8"));
const source = readFileSync(join(root, "frontend/wrangler.jsonc"), "utf8");
const baseName = /"name"\s*:\s*"([^"]+)"/.exec(source)?.[1];

const problems = [];
const expect = (condition, message) => {
  if (!condition) problems.push(message);
};

expect(baseName, "frontend/wrangler.jsonc must declare a base name");
expect(generated.name === `${baseName}-${stage}`, `Worker name is ${generated.name}, expected ${baseName}-${stage}`);
expect(generated.targetEnvironment === stage, `target environment is ${generated.targetEnvironment}, expected ${stage}`);
expect(generated.topLevelName === baseName, `top-level name is ${generated.topLevelName}, expected ${baseName}`);
expect(generated.workers_dev === (stage !== "prod"), `workers_dev is ${generated.workers_dev}; only non-prod stages use workers.dev`);
expect(Object.keys(generated.vars ?? {}).length === 0, "the build must not bake runtime variables; the deploy command writes them");
expect(!/SECRET|TOKEN|PASSWORD/i.test(JSON.stringify(generated.vars ?? {})), "the build must not contain secrets");
expect(generated.assets?.binding === "ASSETS", "the ASSETS binding must be present");

if (problems.length > 0) {
  console.error(`Worker build for stage "${stage}" is wrong:`);
  for (const problem of problems) console.error(`  ${problem}`);
  process.exit(1);
}
console.log(`Worker build for stage "${stage}" is consistent (${generated.name}).`);
