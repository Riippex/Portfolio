// Proves each release invariant by breaking the real repository files in memory and requiring
// the matching violation. A mutation that does not change its file fails loudly, so a stale
// pattern can never turn a test into a silent pass.
//
//   node --test tools/check-release.test.mjs
//
// No real address appears in this file. The privacy tests build a synthetic address from parts
// at run time on a domain that is not reserved, which is what the address scan must catch.

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { CHECKED_FILES, REQUIRED_SECTIONS, checkRelease, loadRepositoryFiles, redact } from "./check-release.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const real = loadRepositoryFiles(root);

const GCP_MAIN = "deployment/gcp/main.tf";
const GCP_VARS = "deployment/gcp/variables.tf";
const RUNBOOK = "docs/runbooks/release.md";
const INVENTORY = "docs/evidence/inventory.json";
const SCHEMA = "docs/evidence/schema.json";
const CI = ".github/workflows/ci.yml";
const DEPLOY = ".github/workflows/deploy.yml";
const JOB_SERVICE = "backend/src/Modules/JobMatching/Rafael.Portfolio.Modules.JobMatching/Application/JobMatchingService.cs";
const JOB_API = "frontend/src/modules/job-matching/api.ts";
const WEB_PROGRAM = "backend/src/Hosts/Web/Rafael.Portfolio.Web/Program.cs";
const WEB_CSPROJ = "backend/src/Hosts/Web/Rafael.Portfolio.Web/Rafael.Portfolio.Web.csproj";
const TRACKED_EXTRA = "tools/check-deployment.mjs"; // tracked, but not a declared file

// A synthetic address on a domain that is NOT reserved. Built from parts so no literal address
// is committed here.
const PROBE_LOCAL = "synthetic.probe";
const PROBE_DOMAIN = "mailbox.synthetic-private.io";
const PROBE = [PROBE_LOCAL, PROBE_DOMAIN].join("@");

function mutate(path, edit) {
  const before = real[path];
  assert.equal(typeof before, "string", `${path} was not loaded`);
  const after = edit(before);
  assert.notEqual(after, before, `the mutation did not change ${path}`);
  return { ...real, [path]: after };
}

const replace = (from, to) => (text) => text.replace(from, to);
const append = (extra) => (text) => `${text}\n${extra}\n`;
const editJson = (change) => (text) => {
  const value = JSON.parse(text);
  change(value);
  return JSON.stringify(value, null, 2);
};

const violationsOf = (files) => checkRelease(files);

// The whole set of diagnostics (or printed text) must withhold the full address, its local part,
// and its domain. Never filter the set first: the leak this guards against is in the diagnostics
// that are NOT about the address.
function assertWithheld(diagnostics, label = "diagnostics") {
  const text = (Array.isArray(diagnostics) ? diagnostics.join("\n") : String(diagnostics)).toLowerCase();
  for (const [what, value] of [["full address", PROBE], ["local part", PROBE_LOCAL], ["domain", PROBE_DOMAIN]]) {
    assert.ok(!text.includes(value.toLowerCase()), `${label} repeated the ${what}`);
  }
}
const hasPrefix = (violations, prefix) => violations.some((violation) => violation.startsWith(prefix));

test("the real repository satisfies every release invariant", () => {
  assert.deepEqual(violationsOf(real), []);
});

// ---------------------------------------------------------------------------------------
// Fail closed: every declared file is required
// ---------------------------------------------------------------------------------------

test("the declared files name the real assistant service, not a nonexistent one", () => {
  const assistant = "backend/src/Modules/Assistant/Rafael.Portfolio.Modules.Assistant/Application/AssistantService.cs";
  assert.ok(CHECKED_FILES.includes(assistant));
  assert.ok(!CHECKED_FILES.some((path) => path.endsWith("StatelessAssistantService.cs")));
  for (const path of CHECKED_FILES) {
    assert.equal(typeof real[path], "string", `declared file ${path} does not exist`);
  }
});

for (const path of CHECKED_FILES) {
  test(`fails closed when a declared file is missing: ${path}`, () => {
    const files = { ...real };
    delete files[path];
    assert.ok(hasPrefix(violationsOf(files), `missing-file:${path}`));
  });
}

test("fails closed when a declared file is empty", () => {
  assert.ok(hasPrefix(violationsOf({ ...real, [GCP_MAIN]: "   \n" }), `empty-file:${GCP_MAIN}`));
});

test("fails closed when every declared file is absent", () => {
  const violations = violationsOf({});
  for (const path of CHECKED_FILES) assert.ok(hasPrefix(violations, `missing-file:${path}`), path);
});

// ---------------------------------------------------------------------------------------
// Privacy: no non-placeholder address, and diagnostics never repeat what they find
// ---------------------------------------------------------------------------------------

for (const [name, path] of [
  ["a declared runbook", RUNBOOK],
  ["a declared source file", WEB_PROGRAM],
  ["a tracked file that is not declared", TRACKED_EXTRA],
]) {
  test(`detects a non-placeholder address in ${name}`, () => {
    const violations = violationsOf(mutate(path, append(`// reach me at ${PROBE}`)));
    assert.ok(hasPrefix(violations, `private-address:${path}`), JSON.stringify(violations));
  });
}

test("diagnostics withhold the matched value, its local part, and its domain", () => {
  const all = violationsOf(mutate(RUNBOOK, append(`Contact: ${PROBE}`)));
  const flagged = all.filter((v) => v.startsWith("private-address:"));
  assert.equal(flagged.length, 1);
  assert.match(flagged[0], /line \d+/);
  assertWithheld(all);
});

test("placeholder addresses on reserved domains are allowed", () => {
  const placeholders = ["a", "b", "c", "d", "e", "f"].map((local, index) =>
    [local, ["example.com", "example.org", "sub.example.net", "host.test", "mail.invalid", "x.localhost"][index]].join("@"));
  const violations = violationsOf(mutate(RUNBOOK, append(placeholders.join("\n"))));
  assert.ok(!hasPrefix(violations, "private-address:"), JSON.stringify(violations));
});

test("package versions and action references are not mistaken for addresses", () => {
  const violations = violationsOf(mutate(CI, append("      - uses: actions/checkout@v4\n      # esbuild@0.21.5 terraform@1.15.8")));
  assert.ok(!hasPrefix(violations, "private-address:"), JSON.stringify(violations));
});

test("the checker and its tests contain no private address", () => {
  const sources = Object.fromEntries(
    ["tools/check-release.mjs", "tools/check-release.test.mjs"].map((path) => [path, readFileSync(join(root, path), "utf8")])
  );
  assert.ok(!hasPrefix(violationsOf(sources), "private-address:"));
});

test("only tracked files are read: ignored private directories are never scanned", () => {
  const paths = Object.keys(real);
  assert.ok(!paths.some((path) => path.startsWith("documents/")), "an ignored private directory was loaded");
  assert.ok(!paths.some((path) => path.includes("node_modules/") || path.includes(".terraform/")));
  assert.ok(paths.length > CHECKED_FILES.length, "tracked files beyond the declared set should be scanned");
});

// ---------------------------------------------------------------------------------------
// Persistence tripwires
// ---------------------------------------------------------------------------------------

for (const [name, path, edit, prefix] of [
  ["a persistence API in backend source", WEB_PROGRAM, append("public class AppDbContext : DbContext {}"), "persistence-tripwire:"],
  ["a database package dependency", WEB_CSPROJ, replace("</Project>", '  <ItemGroup><PackageReference Include="Npgsql" Version="1.0.0" /></ItemGroup>\n</Project>'), "persistence-tripwire:"],
  ["a SQL database resource", GCP_MAIN, append('resource "google_sql_database" "db" {}'), "persistence-tripwire:"],
  ["a storage bucket resource", GCP_MAIN, append('resource "google_storage_bucket" "b" {}'), "persistence-tripwire:"],
  ["a key-value store in the edge stack", "deployment/cloudflare/main.tf", append('resource "cloudflare_workers_kv_namespace" "k" {}'), "persistence-tripwire:"],
]) {
  test(`persistence tripwire detects ${name}`, () => {
    assert.ok(hasPrefix(violationsOf(mutate(path, edit)), prefix));
  });
}

// ---------------------------------------------------------------------------------------
// Runtime, disabled Contact, and the real scaling wiring
// ---------------------------------------------------------------------------------------

for (const [name, edit, prefix] of [
  ["a non-Production remote runtime", replace('aspnetcore_environment = "Production"', 'aspnetcore_environment = "Development"'), "runtime-safety:remote environment"],
  ["a runtime not taken from the local", replace("value = local.aspnetcore_environment", 'value = "Production"'), "runtime-safety:ASPNETCORE_ENVIRONMENT"],
  ["Contact enabled", replace('name  = "Contact__Enabled"\n        value = "false"', 'name  = "Contact__Enabled"\n        value = "true"'), "contact-disabled:Contact__Enabled"],
  ["a contact credential wired in", append('# env contact-api-token\nenv { name = "Contact__ApiToken" }'), "contact-disabled:the service must not depend"],
]) {
  test(`detects ${name}`, () => {
    assert.ok(hasPrefix(violationsOf(mutate(GCP_MAIN, edit)), prefix));
  });
}

for (const [name, path, edit, prefix] of [
  // The bypass a defaults-only check missed: the variable stays 2 but the service ignores it.
  ["continuous CPU allocation", GCP_MAIN, replace("cpu_idle = true", "cpu_idle = false"), "cost-bounds:the backend container must explicitly select"],
  ["CPU billing setting omitted", GCP_MAIN, replace(/\s+cpu_idle = true\n/, "\n"), "cost-bounds:the backend container must explicitly select"],
  ["max_instance_count replaced with 100", GCP_MAIN, replace("max_instance_count = var.max_instances", "max_instance_count = 100"), "cost-bounds:the service must set max_instance_count"],
  ["revision min_instance_count replaced with 1", GCP_MAIN, replace(/(    scaling \{\s*min_instance_count\s*=\s*)var.min_instances/, (_, head) => `${head}1`), "cost-bounds:the service must set min_instance_count"],
  ["cpu replaced with a literal", GCP_MAIN, replace(/cpu(\s+)= var\.cpu_limit/, "cpu$1= \"8000m\""), "cost-bounds:the service must set cpu"],
  ["memory replaced with a literal", GCP_MAIN, replace(/memory(\s+)= var\.memory_limit/, "memory$1= \"8Gi\""), "cost-bounds:the service must set memory"],
  ["max_instances default raised", GCP_VARS, replace(/(variable "max_instances"[\s\S]*?default\s*=\s*)2/, (_, head) => `${head}100`), "cost-bounds:var.max_instances must default to 2"],
  ["min_instances default raised", GCP_VARS, replace(/(variable "min_instances"[\s\S]*?default\s*=\s*)0/, (_, head) => `${head}1`), "cost-bounds:var.min_instances must default to 0"],
  ["memory default raised", GCP_VARS, replace('default     = "512Mi"', 'default     = "8Gi"'), "cost-bounds:var.memory_limit must default to"],
]) {
  test(`cost wiring detects ${name}`, () => {
    assert.ok(hasPrefix(violationsOf(mutate(path, edit)), prefix));
  });
}

test("a Terraform default is not accepted as proof when the service no longer reads it", () => {
  // Defaults all intact, but the service stops reading the instance limits entirely.
  const files = mutate(GCP_MAIN, replace(/scaling \{[\s\S]*?\n {4}\}/, "scaling {\n      max_instance_count = 100\n    }"));
  assert.ok(hasPrefix(violationsOf(files), "cost-bounds:"));
});

// ---------------------------------------------------------------------------------------
// Public evidence against the canonical schema and governance rules
// ---------------------------------------------------------------------------------------

const firstItem = (value) => value.items[0];
const projectItem = (value) => value.items.find((item) => item.kind === "project");

for (const [name, edit, prefix] of [
  ["an item without claims", editJson((v) => delete firstItem(v).claims), "evidence-schema:"],
  ["an unknown evidence kind", editJson((v) => { firstItem(v).kind = "service"; }), "evidence-schema:"],
  ["an unknown evidence status", editJson((v) => { firstItem(v).evidenceStatus = "approved"; }), "evidence-schema:"],
  ["a malformed review date", editJson((v) => { firstItem(v).lastReviewed = "not-a-date"; }), "evidence-schema:"],
  ["an item that is not an object", editJson((v) => { v.items[0] = "profile"; }), "evidence-schema:"],
  ["a claim without a citation", editJson((v) => delete firstItem(v).claims[0].citation), "evidence-schema:"],
  ["a claim with an unknown status", editJson((v) => { firstItem(v).claims[0].status = "maybe"; }), "evidence-schema:"],
  ["a profile without a headline", editJson((v) => delete v.items.find((i) => i.kind === "profile").headline), "evidence-schema:"],
  ["a profile with no focus areas", editJson((v) => { v.items.find((i) => i.kind === "profile").focusAreas = []; }), "evidence-schema:"],
  ["a missing top-level version", editJson((v) => delete v.version), "evidence-schema:"],
  ["no items", editJson((v) => { v.items = []; }), "evidence:inventory has no items"],
  ["a duplicate slug", editJson((v) => { v.items[1].slug = v.items[0].slug; }), "evidence:item"],
  ["a duplicate id", editJson((v) => { v.items[1].id = v.items[0].id; }), "evidence:item"],
  ["a duplicate claim id", editJson((v) => { v.items[1].claims[0].claimId = v.items[0].claims[0].claimId; }), "evidence:item"],
  ["verified evidence without a source URL", editJson((v) => { const item = projectItem(v); item.evidenceStatus = "verified"; item.sourceUrl = null; }), "evidence:item"],
  ["verified evidence with a non-https source URL", editJson((v) => { const item = projectItem(v); item.evidenceStatus = "verified"; item.sourceUrl = "http://example.com/x"; }), "evidence:item"],
  ["a verified claim inside a pending item", editJson((v) => { projectItem(v).claims[0].status = "verified"; }), "evidence:item"],
  ["a citation without an anchor", editJson((v) => { projectItem(v).claims[0].citation = "docs/evidence/projects/x.md"; }), "evidence:item"],
  ["a document path that is not a tracked file", editJson((v) => { projectItem(v).documentPath = "docs/evidence/projects/nonexistent.md"; }), "evidence:item"],
  ["invalid JSON", () => "{ not json", "evidence:inventory.json or schema.json is not valid JSON"],
]) {
  test(`evidence check detects ${name}`, () => {
    assert.ok(hasPrefix(violationsOf(mutate(INVENTORY, edit)), prefix), JSON.stringify(violationsOf(mutate(INVENTORY, edit))));
  });
}

test("a schema that starts using an unsupported keyword is refused rather than half-checked", () => {
  const files = mutate(SCHEMA, editJson((schema) => { schema.properties.version.pattern = "^[0-9]{4}\\.[0-9]{2}$"; }));
  assert.ok(hasPrefix(violationsOf(files), "evidence:schema.json uses keywords this check cannot enforce"));
});

// ---------------------------------------------------------------------------------------
// Fabricated evidence
// ---------------------------------------------------------------------------------------

test("detects a synthetic claim identifier in code", () => {
  assert.ok(hasPrefix(violationsOf(mutate(JOB_SERVICE, append("// fallback claim-verified id"))), "synthetic-claim-id:"));
});

test("detects a fabricated project route in the frontend", () => {
  assert.ok(hasPrefix(violationsOf(mutate(JOB_API, append('const route = "/projects/profile";'))), "bogus-project-route:"));
});

// ---------------------------------------------------------------------------------------
// The release checks run in callable CI, and deployment validation calls that CI
// ---------------------------------------------------------------------------------------

for (const [name, path, edit] of [
  ["the release tests removed from CI", CI, replace("node --test tools/check-release.test.mjs", "true")],
  ["the release checker removed from CI", CI, replace("node tools/check-release.mjs", "true")],
  ["CI no longer callable", CI, replace(/^ {2}workflow_call:\n/m, "")],
  ["deployment validation no longer calling CI", DEPLOY, replace("uses: ./.github/workflows/ci.yml", "uses: ./.github/workflows/other.yml")],
]) {
  test(`detects ${name}`, () => {
    assert.ok(hasPrefix(violationsOf(mutate(path, edit)), "ci-wiring:"));
  });
}

// ---------------------------------------------------------------------------------------
// The runbook states what the release relies on, and no more
// ---------------------------------------------------------------------------------------

for (const section of REQUIRED_SECTIONS) {
  test(`detects a missing runbook section: ${section}`, () => {
    assert.ok(hasPrefix(violationsOf(mutate(RUNBOOK, replace(section, "## Omitted"))), `missing-runbook-section:${section}`));
  });
}

for (const [name, edit, prefix] of [
  ["swapped Cloud Run allowances", replace("180,000 vCPU-seconds, 360,000 GiB-seconds", "360,000 vCPU-seconds, 180,000 GiB-seconds"), "cost-claims:Cloud Run vCPU and memory allowances are swapped"],
  ["wrong Cloud Run allowances", replace("180,000 vCPU-seconds", "240,000 vCPU-seconds"), "cost-claims:request-based Cloud Run allowances"],
  ["an unsupported monthly ceiling", append("Total estimated cost: < $0.50 USD / month"), "cost-claims:an unsupported monthly ceiling"],
  ["a missing retrieval date", replace("**Retrieved: 2026-10-07.**", "**Retrieved recently.**"), "cost-claims:prices need a"],
  ["a missing pricing source", replace("https://cloud.google.com/secret-manager/pricing", "secret-manager-pricing"), "cost-claims:missing pricing source"],
  ["a missing statement that no limit is enforced", replace("No spending limit is enforced", "Spending is bounded"), "cost-claims:state that no spending limit is enforced"],
  ["an undocumented managed secret", replace(/proxy-identity-secret/g, "identity-secret"), "cost-claims:managed secret proxy-identity-secret"],
  ["a documented contact secret", append("The service reads contact-token at startup."), "cost-claims:the runbook documents a contact secret"],
  ["a wrong secret count", append("There are 3 secrets."), "cost-claims:the runbook documents a contact secret"],
  ["a missing egress discussion", replace(/egress/gi, "traffic"), "cost-claims:cost section must cover"],
  ["no note that rejected requests are still billed", replace("rejected requests still execute application code and are billed", "rejected requests are cheap"), "cost-claims:cost section must cover"],
  ["no mention of optional Contact costs", replace(/Email Service/g, "mail provider"), "cost-claims:cost section must cover"],
  ["a pre-flight list that does not gate Contact", replace("- [ ] Contact stays **disabled** in this release", "- [ ] Contact notes recorded for this release"), "privacy-claims:the pre-flight checklist"],
  ["a claim that zero persistence is proven", append("This check proves zero persistence."), "privacy-claims:zero-persistence"],
  ["the old unscoped logging claim", append("Contact content is never logged, cached, or persisted."), "privacy-claims:zero-persistence"],
  ["missing external retention scope", replace(/outside the application/gi, "elsewhere"), "privacy-claims:external mailbox"],
  ["a missing link to the Contact runbook", replace(/contact\.md/g, "contact-notes.md"), "privacy-claims:external mailbox"],
  ["persistence checks not called tripwires", replace(/tripwire/gi, "heuristic"), "privacy-claims:persistence checks"],
  ["Contact not described as owner-authorized", replace(/owner-authorized/gi, "authorized"), "privacy-claims:Contact must be described"],
  ["evidence validation not delegated", replace(/EvidenceSchemaValidationTests/g, "SomeTests"), "privacy-claims:full evidence validation"],
]) {
  test(`runbook check detects ${name}`, () => {
    const violations = violationsOf(mutate(RUNBOOK, edit));
    assert.ok(hasPrefix(violations, prefix), JSON.stringify(violations));
  });
}

// ---------------------------------------------------------------------------------------
// Redaction covers every diagnostic, whichever field or file the address came from
// ---------------------------------------------------------------------------------------

test("redact masks any email-like token, reserved domains included, and leaves other text alone", () => {
  assert.equal(redact(`see ${PROBE} and a@example.com`), "see <address withheld> and <address withheld>");
  assert.equal(redact("items[1].claims[0]: citation has no anchor"), "items[1].claims[0]: citation has no anchor");
  assert.equal(redact("actions/checkout@v4 esbuild@0.21.5"), "actions/checkout@v4 esbuild@0.21.5");
  assert.equal(redact(redact(PROBE)), redact(PROBE), "redaction is idempotent");
});

test("a claimId holding an address and a missing citation anchor: detected, located, and withheld", () => {
  const files = mutate(INVENTORY, editJson((v) => {
    const claim = projectItem(v).claims[0];
    claim.claimId = PROBE;
    claim.citation = "docs/evidence/projects/x.md";
  }));
  const violations = violationsOf(files);
  assert.ok(hasPrefix(violations, `private-address:${INVENTORY}`), "the address must still be detected");
  assert.ok(violations.some((v) => /^evidence:items\[\d+\]\.claims\[0\]: citation has no anchor$/.test(v)), JSON.stringify(violations));
  assertWithheld(violations);
});

test("two slugs holding the same address: detected, located, and withheld", () => {
  const files = mutate(INVENTORY, editJson((v) => { v.items[0].slug = PROBE; v.items[1].slug = PROBE; }));
  const violations = violationsOf(files);
  assert.ok(hasPrefix(violations, `private-address:${INVENTORY}`));
  assert.ok(violations.some((v) => /^evidence:items\[1\]: duplicate slug \(first at items\[0\]\)$/.test(v)), JSON.stringify(violations));
  assertWithheld(violations);
});

test("two items and two claims sharing an address as id and claimId are located by index", () => {
  const files = mutate(INVENTORY, editJson((v) => {
    v.items[0].id = PROBE;
    v.items[1].id = PROBE;
    v.items[0].claims[0].claimId = PROBE;
    v.items[1].claims[0].claimId = PROBE;
  }));
  const violations = violationsOf(files);
  assert.ok(violations.some((v) => /^evidence:items\[1\]: duplicate id \(first at items\[0\]\)$/.test(v)));
  assert.ok(violations.some((v) => /^evidence:items\[1\]\.claims\[0\]: duplicate claimId \(first at items\[0\]\.claims\[0\]\)$/.test(v)));
  assertWithheld(violations);
});

// Plant the address in each inventory field, one at a time; no diagnostic may repeat it.
for (const [field, plant] of [
  ["kind", (v) => { v.items[0].kind = PROBE; }],
  ["evidenceStatus", (v) => { v.items[0].evidenceStatus = PROBE; }],
  ["title", (v) => { v.items[0].title = PROBE; }],
  ["summary", (v) => { v.items[0].summary = PROBE; }],
  ["version", (v) => { v.version = PROBE; }],
  ["lastUpdated", (v) => { v.lastUpdated = PROBE; }],
  ["lastReviewed", (v) => { v.items[0].lastReviewed = PROBE; }],
  ["sourceUrl", (v) => { const item = projectItem(v); item.evidenceStatus = "verified"; item.sourceUrl = PROBE; }],
  ["documentPath", (v) => { projectItem(v).documentPath = PROBE; }],
  ["claim statement", (v) => { v.items[0].claims[0].statement = PROBE; }],
  ["claim status", (v) => { v.items[0].claims[0].status = PROBE; }],
  ["claim citation", (v) => { v.items[0].claims[0].citation = PROBE; }],
  ["focus area", (v) => { v.items.find((i) => i.kind === "profile").focusAreas = [PROBE, 7]; }],
  ["an unexpected key name", (v) => { v.items[0][PROBE] = true; }],
]) {
  test(`an address planted in the inventory ${field} is detected and never repeated`, () => {
    const violations = violationsOf(mutate(INVENTORY, editJson(plant)));
    assert.ok(hasPrefix(violations, `private-address:${INVENTORY}`), "the address must be detected");
    assertWithheld(violations, `diagnostics for ${field}`);
  });
}

test("an address in a file name is detected and never repeated", () => {
  const violations = violationsOf({ ...real, [`notes/${PROBE}.md`]: `reach ${PROBE}` });
  assert.ok(violations.some((v) => v.startsWith("private-address:notes/")));
  assertWithheld(violations);
});

test("an address in the schema, a declared source file, and a declared workflow is never repeated", () => {
  for (const path of [SCHEMA, WEB_PROGRAM, DEPLOY]) {
    const violations = violationsOf(mutate(path, (text) => (path === SCHEMA ? text.replace('"title"', `"x-contact": "${PROBE}", "title"`) : `${text}\n// ${PROBE}\n`)));
    assert.ok(hasPrefix(violations, `private-address:${path}`), path);
    assertWithheld(violations, path);
  }
});

// What the CLI actually prints, from a temporary git tree: the hostile inventory plus every
// planted placement. The tree is deleted afterwards; nothing here touches the real repository.
function withTemporaryRepository(files, run) {
  const base = mkdtempSync(join(tmpdir(), "release-cli-"));
  try {
    for (const [rel, content] of Object.entries(files)) {
      const full = join(base, rel);
      mkdirSync(dirname(full), { recursive: true });
      writeFileSync(full, content);
    }
    assert.equal(spawnSync("git", ["init", "-q"], { cwd: base }).status, 0);
    assert.equal(spawnSync("git", ["add", "-A"], { cwd: base }).status, 0);
    return run(base);
  } finally {
    rmSync(base, { recursive: true, force: true, maxRetries: 3 });
  }
}

test("the CLI prints no part of an address planted across the inventory", () => {
  const hostile = mutate(INVENTORY, editJson((v) => {
    v.items[0].slug = PROBE;
    v.items[1].slug = PROBE;
    v.items[0].id = PROBE;
    v.items[1].id = PROBE;
    const claim = v.items[1].claims[0];
    claim.claimId = PROBE;
    claim.citation = "docs/evidence/projects/x.md";
  }));
  const result = withTemporaryRepository(hostile, (base) =>
    spawnSync(process.execPath, [join(base, "tools", "check-release.mjs")], { encoding: "utf8" }));

  assert.equal(result.status, 1, `${result.stdout}${result.stderr}`);
  const printed = `${result.stdout}\n${result.stderr}`;
  assert.match(printed, /private-address:/);
  assert.match(printed, /evidence:items\[1\]/);
  assertWithheld(printed, "CLI output");
});

test("the CLI error path redacts an address that appears in a path or message", () => {
  // No git repository here, so loading fails; the base directory name itself holds the address.
  const base = join(mkdtempSync(join(tmpdir(), "release-cli-err-")), PROBE);
  mkdirSync(join(base, "tools"), { recursive: true });
  try {
    writeFileSync(join(base, "tools", "check-release.mjs"), readFileSync(join(root, "tools", "check-release.mjs")));
    const result = spawnSync(process.execPath, [join(base, "tools", "check-release.mjs")], { encoding: "utf8" });
    const printed = `${result.stdout}\n${result.stderr}`;
    assert.equal(result.status, 1, printed);
    assert.match(printed, /could not run/);
    assertWithheld(printed, "CLI error output");
  } finally {
    rmSync(dirname(base), { recursive: true, force: true, maxRetries: 3 });
  }
});

test("every mutation case has a distinct name", () => {
  // Guards the table-driven tests above from silently shadowing each other.
  const names = [...REQUIRED_SECTIONS, ...CHECKED_FILES];
  assert.equal(new Set(names).size, names.length);
});
