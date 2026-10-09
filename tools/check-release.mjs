#!/usr/bin/env node
// Offline release checks for the Portfolio repository. Files only: no network, no provider
// state, no credentials.
//
//   node tools/check-release.mjs          check the repository, exit 1 on a violation
//
// What this establishes, and what it does not (the release runbook repeats this):
//   * It checks that declared files exist and are wired as the release relies on, that the
//     public evidence inventory satisfies the canonical schema and governance rules, that the
//     Terraform service really takes its scaling and size limits from the variables it checks,
//     and that no tracked file carries a non-placeholder email address.
//   * Its persistence checks are tripwires: a lexical or dependency match proves a regression,
//     but the absence of a match is NOT proof of zero persistence.
//   * Terraform instance limits bound the number of instances. They are not a spending limit,
//     and nothing here enforces one.
//   * Full JSON Schema validation and citation resolution are delegated to the unit tests
//     (EvidenceSchemaValidationTests, EvidenceInventoryTests, EvidenceIngestionTests) that the
//     same CI run executes. This file validates a documented subset and refuses to pass if the
//     schema starts using a keyword it does not understand.
//
// checkRelease(files) takes a { relativePath: contents } map and returns violation strings,
// so tools/check-release.test.mjs can prove each invariant by breaking it. Diagnostics name
// files and lines, never matched private values.

import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, statSync } from "node:fs";
import { dirname, extname, join } from "node:path";
import { fileURLToPath } from "node:url";

// Every declared file is required: a missing one is a violation, never a silent skip.
export const CHECKED_FILES = [
  ".github/workflows/ci.yml",
  ".github/workflows/deploy.yml",
  "docs/runbooks/release.md",
  "docs/runbooks/contact.md",
  "docs/evidence/inventory.json",
  "docs/evidence/schema.json",
  "docs/data-handling.md",
  "docs/architecture.md",
  "deployment/gcp/main.tf",
  "deployment/gcp/variables.tf",
  "deployment/cloudflare/main.tf",
  "deployment/cloudflare/variables.tf",
  "frontend/wrangler.jsonc",
  "frontend/src/modules/contact/api.ts",
  "frontend/src/modules/job-matching/api.ts",
  "backend/src/Hosts/Web/Rafael.Portfolio.Web/Program.cs",
  "backend/src/Modules/Contact/Rafael.Portfolio.Modules.Contact/Domain/ContactMessage.cs",
  "backend/src/Modules/Contact/Rafael.Portfolio.Modules.Contact/Infrastructure/CloudflareEmailRelay.cs",
  "backend/src/Modules/Assistant/Rafael.Portfolio.Modules.Assistant/Application/AssistantService.cs",
  "backend/src/Modules/JobMatching/Rafael.Portfolio.Modules.JobMatching/Application/JobMatchingService.cs",
];

// Source files scanned for forbidden persistence APIs and fabricated evidence.
const isSourcePath = (path) => path.startsWith("backend/src/") || path.startsWith("frontend/src/");

const TEXT_EXTENSIONS = new Set([
  ".md", ".mjs", ".js", ".ts", ".tsx", ".cs", ".csproj", ".json", ".jsonc", ".tf",
  ".yml", ".yaml", ".example", ".props", ".sln", ".ps1", ".css", ".html", ".txt", ".sh",
]);
const SKIPPED_TRACKED = [/(^|\/)package-lock\.json$/, /\.terraform\.lock\.hcl$/];
const MAX_SCAN_BYTES = 1024 * 1024;

const FORBIDDEN_PERSISTENCE_TOKENS = [
  "DbContext", "IMongoCollection", "AmazonDynamoDB", "Npgsql", "Microsoft.EntityFrameworkCore", "sqlite3",
];
const FORBIDDEN_PACKAGES = /EntityFramework|Npgsql|Dapper|MongoDB|Sqlite|StackExchange\.Redis|Microsoft\.Data\.SqlClient|CosmosDB|Cosmos|DynamoDB|Firestore/i;
const FORBIDDEN_RESOURCES =
  /resource\s+"(google_sql_[a-z_]+|google_firestore_[a-z_]+|google_redis_[a-z_]+|google_bigtable_[a-z_]+|google_spanner_[a-z_]+|google_storage_bucket|cloudflare_d1_database|cloudflare_workers_kv[a-z_]*|cloudflare_r2_bucket|cloudflare_durable_object[a-z_]*)"/g;

// The one approved durable operational record is the Assistant's model control ledger
// (docs/runbooks/model-control.md): a Firestore database owned by a single Terraform stack and
// reached only by the Assistant module's adapter. These exceptions are exact: the same package
// or resource anywhere else is still a regression.
export const APPROVED_PERSISTENCE = {
  packages: {
    "backend/src/Modules/Assistant/Rafael.Portfolio.Modules.Assistant/Rafael.Portfolio.Modules.Assistant.csproj": ["Google.Cloud.Firestore"],
  },
  resources: {
    "deployment/control-ledger/main.tf": ["google_firestore_database", "google_firestore_field"],
  },
};

export const REQUIRED_SECTIONS = [
  "## Release Governance and Boundary",
  "## Pre-flight Checklist",
  "## Privacy & Data Classification Audit",
  "## Contact Activation Boundary",
  "## Cost Estimate & Spend Controls",
  "## What the Offline Checks Establish",
  "## End-to-End Release Smoke Execution",
  "## Release Promotion Procedure",
  "## Rollback Procedure",
];

const PRICING_SOURCES = [
  "https://cloud.google.com/run/pricing",
  "https://cloud.google.com/secret-manager/pricing",
  "https://cloud.google.com/artifact-registry/pricing",
  "https://developers.cloudflare.com/workers/platform/pricing/",
];

// ---------------------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------------------

function stripHcl(text) {
  return text
    .replace(/\/\*[\s\S]*?\*\//g, "")
    .split("\n")
    .filter((line) => !/^\s*(#|\/\/)/.test(line))
    .join("\n");
}

// Body of the first `<header> { ... }` block, balancing braces.
function hclBlock(text, header) {
  const start = text.search(header);
  if (start < 0) return null;
  const open = text.indexOf("{", start);
  if (open < 0) return null;
  let depth = 0;
  for (let i = open; i < text.length; i += 1) {
    if (text[i] === "{") depth += 1;
    else if (text[i] === "}") {
      depth -= 1;
      if (depth === 0) return text.slice(open + 1, i);
    }
  }
  return null;
}

function variableDefault(variablesTf, name) {
  const block = hclBlock(variablesTf, new RegExp(`variable\\s+"${name}"`));
  return block === null ? null : (/\bdefault\s*=\s*("[^"]*"|[^\s]+)/.exec(block)?.[1] ?? null);
}

// An address is a placeholder only on a reserved domain (RFC 2606 / RFC 6761).
function isReservedDomain(domain) {
  const lower = domain.toLowerCase();
  if (/(^|\.)example\.(com|org|net)$/.test(lower)) return true;
  return ["test", "invalid", "example", "localhost"].includes(lower.split(".").pop());
}

// Email-like tokens. The same pattern drives detection and redaction, so what the scan can find
// is exactly what the diagnostics cannot repeat.
const EMAIL_PATTERN = /[A-Za-z0-9._%+-]+@([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,})/g;

/**
 * Centralized redaction: every violation returned by checkRelease, and every line the CLI prints,
 * passes through this. It masks any email-like token (reserved domains too, which keeps the rule
 * simple), so no diagnostic can repeat an address whatever field or file it came from. Diagnostics
 * also avoid interpolating inventory values in the first place and locate problems by index; this
 * is the backstop for anything else, such as a file name or an error message.
 */
export function redact(text) {
  return String(text).replace(EMAIL_PATTERN, "<address withheld>");
}

// Lines containing an email-like token on a non-reserved domain. Values are never returned.
function privateAddressLines(content) {
  const lines = [];
  for (const match of content.matchAll(EMAIL_PATTERN)) {
    if (!isReservedDomain(match[1])) {
      lines.push(content.slice(0, match.index).split("\n").length);
    }
  }
  return lines;
}

const JSON_SCHEMA_KEYWORDS = new Set([
  "$schema", "allOf", "const", "enum", "format", "if", "items", "minItems", "properties", "required", "then", "title", "type",
]);

function collectKeywords(schema, found = new Set()) {
  if (Array.isArray(schema)) {
    schema.forEach((entry) => collectKeywords(entry, found));
  } else if (schema && typeof schema === "object") {
    for (const [key, value] of Object.entries(schema)) {
      found.add(key);
      if (key === "properties") Object.values(value).forEach((entry) => collectKeywords(entry, found));
      else if (["items", "if", "then", "allOf"].includes(key)) collectKeywords(value, found);
    }
  }
  return found;
}

const typeMatches = (value, type) =>
  ({
    string: typeof value === "string",
    number: typeof value === "number",
    integer: Number.isInteger(value),
    boolean: typeof value === "boolean",
    null: value === null,
    array: Array.isArray(value),
    object: value !== null && typeof value === "object" && !Array.isArray(value),
  })[type] ?? false;

// A subset validator over exactly the keywords JSON_SCHEMA_KEYWORDS names.
function validateAgainstSchema(schema, value, at, out) {
  if (schema.type !== undefined) {
    const types = [].concat(schema.type);
    if (!types.some((type) => typeMatches(value, type))) {
      out.push(`${at}: expected ${types.join("|")}`);
      return;
    }
  }
  if (schema.const !== undefined && value !== schema.const) out.push(`${at}: must equal ${JSON.stringify(schema.const)}`);
  if (schema.enum && !schema.enum.includes(value)) out.push(`${at}: must be one of ${schema.enum.join(", ")}`);
  if (schema.format === "date") {
    const valid = typeof value === "string" && /^\d{4}-\d{2}-\d{2}$/.test(value) && !Number.isNaN(Date.parse(value));
    if (!valid) out.push(`${at}: must be an ISO date`);
  }
  if (typeMatches(value, "object")) {
    for (const key of schema.required ?? []) {
      if (!(key in value)) out.push(`${at}: missing "${key}"`);
    }
    for (const [key, sub] of Object.entries(schema.properties ?? {})) {
      if (key in value) validateAgainstSchema(sub, value[key], `${at}.${key}`, out);
    }
  }
  if (Array.isArray(value)) {
    if (schema.minItems !== undefined && value.length < schema.minItems) out.push(`${at}: needs at least ${schema.minItems} item(s)`);
    if (schema.items) value.forEach((entry, index) => validateAgainstSchema(schema.items, entry, `${at}[${index}]`, out));
  }
  for (const sub of schema.allOf ?? []) {
    if (sub.if) {
      const probe = [];
      validateAgainstSchema(sub.if, value, at, probe);
      if (probe.length === 0 && sub.then) validateAgainstSchema(sub.then, value, at, out);
    } else {
      validateAgainstSchema(sub, value, at, out);
    }
  }
}

// ---------------------------------------------------------------------------------------
// The checks
// ---------------------------------------------------------------------------------------

export function checkRelease(files) {
  const violations = [];
  const fail = (id, detail) => violations.push(redact(`${id}:${detail}`));

  // 0. Every declared file must exist and have content (fail closed) ----------------------
  for (const rel of CHECKED_FILES) {
    if (typeof files[rel] !== "string") fail("missing-file", rel);
    else if (files[rel].trim() === "") fail("empty-file", rel);
  }
  const text = (rel) => (typeof files[rel] === "string" ? files[rel] : "");

  // 1. No private address in any tracked public file --------------------------------------
  // Only addresses on reserved placeholder domains are allowed. The matched value is never
  // reported, so the diagnostic cannot itself spread what it found.
  for (const [path, content] of Object.entries(files)) {
    const lines = privateAddressLines(content);
    if (lines.length > 0) {
      fail("private-address", `${path} line ${[...new Set(lines)].join(",")} (value withheld; use a reserved placeholder domain)`);
    }
  }

  // 2. Persistence tripwires (evidence of a regression, not proof of absence) -------------
  for (const [path, content] of Object.entries(files)) {
    if (isSourcePath(path)) {
      for (const token of FORBIDDEN_PERSISTENCE_TOKENS) {
        if (content.includes(token)) fail("persistence-tripwire", `${path} references ${token}`);
      }
    }
    if (path.endsWith(".csproj")) {
      for (const match of content.matchAll(/<PackageReference\s+Include="([^"]+)"/g)) {
        if (FORBIDDEN_PACKAGES.test(match[1]) && !(APPROVED_PERSISTENCE.packages[path] ?? []).includes(match[1])) {
          fail("persistence-tripwire", `${path} depends on ${match[1]}`);
        }
      }
    }
    if (path.endsWith(".tf")) {
      for (const resource of stripHcl(content).matchAll(FORBIDDEN_RESOURCES)) {
        if (!(APPROVED_PERSISTENCE.resources[path] ?? []).includes(resource[1])) {
          fail("persistence-tripwire", `${path} declares ${resource[1]}`);
        }
      }
    }
  }

  // 3. Production runtime and the disabled Contact feature, from the real wiring ----------
  const gcpMain = stripHcl(text("deployment/gcp/main.tf"));
  if (!/aspnetcore_environment\s*=\s*"Production"/.test(gcpMain)) fail("runtime-safety", "remote environment is not Production");
  if (!/name\s*=\s*"ASPNETCORE_ENVIRONMENT"\s*\n\s*value\s*=\s*local\.aspnetcore_environment/.test(gcpMain)) {
    fail("runtime-safety", "ASPNETCORE_ENVIRONMENT is not set from local.aspnetcore_environment");
  }
  if (!/name\s*=\s*"Contact__Enabled"\s*\n\s*value\s*=\s*"false"/.test(gcpMain)) {
    fail("contact-disabled", "Contact__Enabled must be explicitly false on the service");
  }
  if (/Contact__ApiToken|contact[-_]api[-_]token/.test(gcpMain)) {
    fail("contact-disabled", "the service must not depend on a contact credential while Contact is disabled");
  }

  // 4. Scaling and size limits are wired from the variables that carry the bounds ---------
  const service = hclBlock(gcpMain, /resource\s+"google_cloud_run_v2_service"\s+"backend"/) ?? "";
  const template = hclBlock(service, /\btemplate\s*\{/) ?? "";
  const revisionScaling = hclBlock(template, /\bscaling\s*\{/) ?? "";
  const container = hclBlock(template, /\bcontainers\s*\{/) ?? "";
  const resources = hclBlock(container, /\bresources\s*\{/) ?? "";
  if (!/^\s*cpu_idle\s*=\s*true\s*$/m.test(resources)) {
    fail("cost-bounds", "the backend container must explicitly select request-based CPU billing");
  }
  const gcpVariables = stripHcl(text("deployment/gcp/variables.tf"));
  for (const [attribute, variable, expected] of [
    ["min_instance_count", "min_instances", "0"],
    ["max_instance_count", "max_instances", "2"],
    ["cpu", "cpu_limit", '"1000m"'],
    ["memory", "memory_limit", '"512Mi"'],
  ]) {
    const scope = attribute.endsWith("instance_count") ? revisionScaling : resources;
    if (!new RegExp(`\\b${attribute}\\s*=\\s*var\\.${variable}\\b`).test(scope)) {
      fail("cost-bounds", `the service must set ${attribute} from var.${variable}`);
    }
    if (variableDefault(gcpVariables, variable) !== expected) {
      fail("cost-bounds", `var.${variable} must default to ${expected}`);
    }
  }

  // 5. Managed secrets are exactly those the runbook documents ---------------------------
  const secretsBlock = /runtime_secrets\s*=\s*\{([^}]*)\}/.exec(gcpMain)?.[1] ?? "";
  const managedSecrets = [...secretsBlock.matchAll(/=\s*"([a-z0-9-]+)"/g)].map((match) => match[1]);
  if (managedSecrets.length === 0) fail("secrets", "no managed runtime secrets found in deployment/gcp/main.tf");

  // 6. Public evidence against the canonical schema and governance rules -----------------
  checkEvidence(files, fail);

  // 7. Fabricated evidence never ships ---------------------------------------------------
  for (const rel of CHECKED_FILES) {
    const content = text(rel);
    if (content.includes("claim-verified")) fail("synthetic-claim-id", rel);
    if (content.includes("/projects/profile")) fail("bogus-project-route", rel);
  }

  // 8. The release checks run in callable CI, and deploy validation calls that CI --------
  const ci = text(".github/workflows/ci.yml");
  for (const command of ["node tools/check-release.mjs", "node --test tools/check-release.test.mjs"]) {
    if (!new RegExp(`^\\s*(-\\s*)?(run:\\s*)?${command.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}\\s*$`, "m").test(ci)) {
      fail("ci-wiring", `ci.yml must run "${command}"`);
    }
  }
  if (!/^\s*workflow_call:/m.test(ci)) fail("ci-wiring", "ci.yml must remain callable (workflow_call)");
  if (!/uses:\s*\.\/\.github\/workflows\/ci\.yml/.test(text(".github/workflows/deploy.yml"))) {
    fail("ci-wiring", "deploy.yml must validate by calling ci.yml at the deployed commit");
  }

  // 9. The release runbook says what it relies on, and no more ---------------------------
  checkRunbook(text("docs/runbooks/release.md"), managedSecrets, fail);

  return violations;
}

function checkEvidence(files, fail) {
  const inventoryText = files["docs/evidence/inventory.json"];
  const schemaText = files["docs/evidence/schema.json"];
  if (typeof inventoryText !== "string" || typeof schemaText !== "string") return; // reported as missing

  let inventory;
  let schema;
  try {
    inventory = JSON.parse(inventoryText);
    schema = JSON.parse(schemaText);
  } catch {
    fail("evidence", "inventory.json or schema.json is not valid JSON");
    return;
  }

  const unsupported = [...collectKeywords(schema)].filter((keyword) => !JSON_SCHEMA_KEYWORDS.has(keyword));
  if (unsupported.length > 0) {
    fail("evidence", `schema.json uses keywords this check cannot enforce (${unsupported.join(", ")}); extend the check before relying on it`);
    return;
  }

  const issues = [];
  validateAgainstSchema(schema, inventory, "inventory", issues);
  for (const issue of issues) fail("evidence-schema", issue);
  if (!Array.isArray(inventory.items) || inventory.items.length === 0) {
    fail("evidence", "inventory has no items");
    return;
  }

  // Governance (docs/evidence/README.md): deny by default. Problems are located by index
  // (items[2].claims[0]) rather than by slug, id, or claimId, because those values come from
  // the inventory and a malformed or hostile one could put anything in them.
  const firstId = new Map();
  const firstSlug = new Map();
  const firstClaim = new Map();
  inventory.items.forEach((item, index) => {
    if (item === null || typeof item !== "object") return;
    const at = `items[${index}]`;

    if (firstId.has(item.id)) fail("evidence", `${at}: duplicate id (first at items[${firstId.get(item.id)}])`);
    else firstId.set(item.id, index);
    if (firstSlug.has(item.slug)) fail("evidence", `${at}: duplicate slug (first at items[${firstSlug.get(item.slug)}])`);
    else firstSlug.set(item.slug, index);

    if (item.evidenceStatus === "verified" && !(typeof item.sourceUrl === "string" && item.sourceUrl.startsWith("https://"))) {
      fail("evidence", `${at}: verified evidence needs a public https sourceUrl`);
    }
    if (typeof item.documentPath === "string" && typeof files[item.documentPath] !== "string") {
      fail("evidence", `${at}: documentPath is not a tracked file`);
    }

    (Array.isArray(item.claims) ? item.claims : []).forEach((claim, claimIndex) => {
      if (claim === null || typeof claim !== "object") return;
      const claimAt = `${at}.claims[${claimIndex}]`;
      if (firstClaim.has(claim.claimId)) fail("evidence", `${claimAt}: duplicate claimId (first at ${firstClaim.get(claim.claimId)})`);
      else firstClaim.set(claim.claimId, claimAt);
      if (claim.status === "verified" && item.evidenceStatus !== "verified") {
        fail("evidence", `${claimAt}: claim is verified but its item is not`);
      }
      if (typeof claim.citation === "string" && !claim.citation.includes("#")) {
        fail("evidence", `${claimAt}: citation has no anchor`);
      }
    });
  });
}

function checkRunbook(runbook, managedSecrets, fail) {
  for (const section of REQUIRED_SECTIONS) {
    if (!runbook.includes(section)) fail("missing-runbook-section", section);
  }

  const cost = runbook.split("## Cost Estimate & Spend Controls")[1]?.split(/\n## /)[0] ?? "";

  // Cost claims: sourced, dated, correct, and honest about what is enforced.
  if (!/\bretrieved:?\s+20\d{2}-\d{2}-\d{2}/i.test(cost)) fail("cost-claims", "prices need a 'Retrieved: YYYY-MM-DD' date");
  for (const source of PRICING_SOURCES) {
    if (!cost.includes(source)) fail("cost-claims", `missing pricing source ${source}`);
  }
  if (!cost.includes("180,000 vCPU-seconds") || !cost.includes("360,000 GiB-seconds") || !cost.includes("2 million requests")) {
    fail("cost-claims", "request-based Cloud Run allowances must be 180,000 vCPU-seconds, 360,000 GiB-seconds, and 2 million requests");
  }
  if (/360,000\s+vCPU|180,000\s+GiB/.test(runbook)) fail("cost-claims", "Cloud Run vCPU and memory allowances are swapped");
  if (/<\s*\$\s?0\.50|\$\s?0\.50\s*(USD)?\s*\/\s*month|USD\s*0\.50/i.test(runbook)) {
    fail("cost-claims", "an unsupported monthly ceiling is stated");
  }
  if (!/No spending limit is enforced/.test(cost)) fail("cost-claims", "state that no spending limit is enforced");
  for (const needle of [/assumption/i, /Worker CPU|CPU time/i, /egress/i, /logging/i, /Artifact Registry/i, /Secret Manager/i, /Email Service/i, /rejected requests still/i]) {
    if (!needle.test(cost)) fail("cost-claims", `cost section must cover ${needle.source}`);
  }
  for (const secret of managedSecrets) {
    if (!runbook.includes(secret)) fail("cost-claims", `managed secret ${secret} is not documented`);
  }
  if (/contact-token|contact-api-token|three secrets|3 secrets/i.test(runbook)) {
    fail("cost-claims", "the runbook documents a contact secret or a wrong secret count");
  }

  // The pre-flight list must gate Contact activation, not only describe it elsewhere.
  const preflight = runbook.split("## Pre-flight Checklist")[1]?.split(/\n## /)[0] ?? "";
  if (!/Contact/.test(preflight) || !/disabled/i.test(preflight) || !preflight.includes("contact.md")) {
    fail("privacy-claims", "the pre-flight checklist must keep Contact disabled unless the activation prerequisites are complete");
  }

  // Privacy claims: scoped to the application.
  if (/proves?\s+zero|guarantees?\s+zero|never\s+logged,\s+cached,\s+or\s+persisted/i.test(runbook)) {
    fail("privacy-claims", "zero-persistence must not be stated as proven or as a guarantee beyond the application");
  }
  if (!/outside the application/i.test(runbook) || !runbook.includes("contact.md")) {
    fail("privacy-claims", "external mailbox and provider retention must be stated and linked to the Contact runbook");
  }
  if (!/tripwire/i.test(runbook)) fail("privacy-claims", "persistence checks must be described as tripwires");
  if (!/model control ledger/i.test(runbook) || !runbook.includes("model-control.md") || !/not a total invoice cap/i.test(runbook)) {
    fail("privacy-claims", "the approved model control ledger exception and its cost-gate limit must be documented and linked");
  }
  if (!/owner-authorized/i.test(runbook) || !/disabled/i.test(runbook)) {
    fail("privacy-claims", "Contact must be described as disabled until owner-authorized activation");
  }
  if (!runbook.includes("EvidenceSchemaValidationTests")) {
    fail("privacy-claims", "full evidence validation must be explicitly delegated to the unit tests");
  }
}

// ---------------------------------------------------------------------------------------
// CLI
// ---------------------------------------------------------------------------------------

// Loads the declared files, plus every TRACKED text file for the address scan. Untracked and
// ignored paths (for example the private documents/ directory) are never read.
export function loadRepositoryFiles(root) {
  const files = {};
  const read = (rel) => {
    const full = join(root, rel);
    if (existsSync(full)) files[rel] = readFileSync(full, "utf8");
  };
  CHECKED_FILES.forEach(read);

  const listing = execFileSync("git", ["-C", root, "ls-files", "-z"], { encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
  for (const rel of listing.split("\0").filter(Boolean)) {
    if (rel in files || !TEXT_EXTENSIONS.has(extname(rel)) || SKIPPED_TRACKED.some((pattern) => pattern.test(rel))) continue;
    const full = join(root, rel);
    if (existsSync(full) && statSync(full).size <= MAX_SCAN_BYTES) files[rel] = readFileSync(full, "utf8");
  }
  return files;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const root = join(dirname(fileURLToPath(import.meta.url)), "..");
  let violations;
  let scanned = 0;
  try {
    const files = loadRepositoryFiles(root);
    scanned = Object.keys(files).length;
    violations = checkRelease(files);
  } catch (error) {
    console.error(redact(`Release check could not run: ${error.message}`));
    process.exit(1);
  }
  if (violations.length > 0) {
    console.error(`Release check violations (${violations.length}):`);
    for (const violation of violations) console.error(redact(`  - ${violation}`));
    process.exit(1);
  }
  console.log(`Release checks passed (${CHECKED_FILES.length} declared files, ${scanned} tracked files scanned for addresses).`);
}
