// Proves each release invariant by breaking real repository definitions in memory and
// requiring the matching violation. A mutation that does not change its file fails loudly.
//
//   node --test tools/check-release.test.mjs

import assert from "node:assert/strict";
import { dirname, join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { checkRelease, loadRepositoryFiles } from "./check-release.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const real = loadRepositoryFiles(root);

const GCP_MAIN = "deployment/gcp/main.tf";
const GCP_VARS = "deployment/gcp/variables.tf";
const RUNBOOK = "docs/runbooks/release.md";
const INVENTORY = "docs/evidence/inventory.json";
const JOB_SERVICE = "backend/src/Modules/JobMatching/Rafael.Portfolio.Modules.JobMatching/Application/JobMatchingService.cs";
const JOB_API = "frontend/src/modules/job-matching/api.ts";
const WEB_PROGRAM = "backend/src/Hosts/Web/Rafael.Portfolio.Web/Program.cs";

function mutate(path, edit) {
  const before = real[path];
  assert.equal(typeof before, "string", `${path} was not loaded`);
  const after = edit(before);
  assert.notEqual(after, before, `the mutation did not change ${path}`);
  return { ...real, [path]: after };
}

const replace = (from, to) => (text) => text.replace(from, to);
const append = (extra) => (text) => `${text}\n${extra}\n`;

test("the real repository files satisfy every release invariant", () => {
  assert.deepEqual(checkRelease(real), []);
});

const cases = [
  // Privacy & Zero-persistence
  [
    "detects: private email leak in documentation",
    RUNBOOK,
    append("Contact: rafaelpatinodiaz.dev@gmail.com"),
    "private-email-leak:",
  ],
  [
    "detects: forbidden persistence pattern in backend",
    WEB_PROGRAM,
    append("public class AppDbContext : DbContext {}"),
    "forbidden-persistence:",
  ],
  [
    "detects: remote runtime environment not Production",
    GCP_MAIN,
    replace('aspnetcore_environment = "Production"', 'aspnetcore_environment = "Development"'),
    "runtime-safety:remote-environment-not-production",
  ],
  [
    "detects: forbidden database resource in GCP Terraform",
    GCP_MAIN,
    append('resource "google_sql_database" "db" {}'),
    "forbidden-cloud-storage:google_sql_database",
  ],

  // Cost Mitigation Bounds
  [
    "detects: min_instances not zero",
    GCP_VARS,
    replace('variable "min_instances" {\n  type        = number\n  description = "Minimum Cloud Run instances. Defaults to 0 for scale-to-zero cost control."\n  default     = 0', 'variable "min_instances" {\n  type        = number\n  description = "Minimum Cloud Run instances. Defaults to 0 for scale-to-zero cost control."\n  default     = 1'),
    "cost-bounds:min-instances-not-zero",
  ],
  [
    "detects: max_instances exceeding burst bound",
    GCP_VARS,
    replace('variable "max_instances" {\n  type        = number\n  description = "Maximum Cloud Run instances. Bounded for abuse and cost mitigation."\n  default     = 2', 'variable "max_instances" {\n  type        = number\n  description = "Maximum Cloud Run instances. Bounded for abuse and cost mitigation."\n  default     = 5'),
    "cost-bounds:max-instances-not-bounded",
  ],

  // Canonical Evidence Grounding
  [
    "detects: synthetic claim ID introduced in code",
    JOB_SERVICE,
    append('// fallback claim-verified id'),
    "synthetic-claim-id:",
  ],
  [
    "detects: bogus project route fabricated in frontend",
    JOB_API,
    append('const route = "/projects/profile";'),
    "bogus-project-route:",
  ],
  [
    "detects: evidence inventory schema mismatch",
    INVENTORY,
    replace('"./schema.json"', '"./other-schema.json"'),
    "evidence:schema-or-version-mismatch",
  ],

  // Release Runbook Completeness
  [
    "detects: missing rollback procedure in runbook",
    RUNBOOK,
    replace("## Rollback Procedure", "## Omitted Procedure"),
    "missing-runbook-section:## Rollback Procedure",
  ],
  [
    "detects: missing pre-flight checklist in runbook",
    RUNBOOK,
    replace("## Pre-flight Checklist", "## Omitted Checklist"),
    "missing-runbook-section:## Pre-flight Checklist",
  ],
];

for (const [name, path, edit, expectedPrefix] of cases) {
  test(name, () => {
    const mutated = mutate(path, edit);
    const violations = checkRelease(mutated);
    assert.ok(
      violations.some((v) => v.startsWith(expectedPrefix)),
      `expected a violation starting with "${expectedPrefix}", got: ${JSON.stringify(violations)}`
    );
  });
}
