#!/usr/bin/env node
// Offline release checks for the Portfolio repository.
// Verifies privacy governance, zero application persistence, evidence integrity,
// cost mitigation bounds, and release runbook readiness.
//
//   node tools/check-release.mjs          check the repository, exit 1 on a violation
//
// checkRelease(files) takes a { relativePath: contents } map and returns violation
// strings, so tools/check-release.test.mjs can prove each invariant by breaking it.

import { existsSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

export const CHECKED_FILES = [
  "docs/runbooks/release.md",
  "docs/evidence/inventory.json",
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
  "backend/src/Modules/Assistant/Rafael.Portfolio.Modules.Assistant/Application/StatelessAssistantService.cs",
  "backend/src/Modules/JobMatching/Rafael.Portfolio.Modules.JobMatching/Application/JobMatchingService.cs",
];

const FORBIDDEN_PRIVATE_EMAILS = [
  "rafaelpatinodiaz.dev@gmail.com",
];

const FORBIDDEN_PERSISTENCE_PATTERNS = [
  "DbContext",
  "IMongoCollection",
  "AmazonDynamoDB",
  "Npgsql",
  "Microsoft.EntityFrameworkCore",
  "sqlite3",
];

const FORBIDDEN_DATABASE_RESOURCES = [
  "google_sql_database",
  "google_firestore_database",
  "google_redis_instance",
  "cloudflare_d1_database",
];

export function loadRepositoryFiles(root) {
  const files = {};
  for (const rel of CHECKED_FILES) {
    const full = join(root, rel);
    if (existsSync(full)) {
      files[rel] = readFileSync(full, "utf8");
    }
  }
  return files;
}

export function checkRelease(files) {
  const violations = [];

  const mustGet = (rel) => {
    const text = files[rel];
    if (typeof text !== "string") {
      violations.push(`missing-file:${rel}`);
      return "";
    }
    return text;
  };

  // 1. Privacy & Zero-persistence Invariants
  for (const [path, content] of Object.entries(files)) {
    for (const email of FORBIDDEN_PRIVATE_EMAILS) {
      if (content.includes(email)) {
        violations.push(`private-email-leak:${path}:${email}`);
      }
    }

    if (path.startsWith("backend/src/") || path.startsWith("frontend/src/")) {
      for (const pattern of FORBIDDEN_PERSISTENCE_PATTERNS) {
        if (content.includes(pattern)) {
          violations.push(`forbidden-persistence:${path}:${pattern}`);
        }
      }
    }
  }

  // 2. Production Runtime Safety
  const gcpMain = mustGet("deployment/gcp/main.tf");
  if (!gcpMain.includes('aspnetcore_environment = "Production"')) {
    violations.push("runtime-safety:remote-environment-not-production");
  }

  for (const res of FORBIDDEN_DATABASE_RESOURCES) {
    if (gcpMain.includes(res)) {
      violations.push(`forbidden-cloud-storage:${res}`);
    }
  }

  // 3. Cost Mitigation Bounds
  const gcpVars = mustGet("deployment/gcp/variables.tf");
  if (!/variable\s+"min_instances"\s*\{[^}]*default\s*=\s*0/s.test(gcpVars)) {
    violations.push("cost-bounds:min-instances-not-zero");
  }
  if (!/variable\s+"max_instances"\s*\{[^}]*default\s*=\s*2/s.test(gcpVars)) {
    violations.push("cost-bounds:max-instances-not-bounded");
  }
  if (!/variable\s+"memory_limit"\s*\{[^}]*default\s*=\s*"512Mi"/s.test(gcpVars)) {
    violations.push("cost-bounds:memory-limit-not-minimal");
  }

  // 4. Public Evidence Grounding & Integrity
  const inventoryText = mustGet("docs/evidence/inventory.json");
  if (inventoryText) {
    try {
      const inventory = JSON.parse(inventoryText);
      if (inventory["$schema"] !== "./schema.json" || typeof inventory.version !== "string") {
        violations.push("evidence:schema-or-version-mismatch");
      }
      if (!Array.isArray(inventory.items) || inventory.items.length === 0) {
        violations.push("evidence:no-items-in-inventory");
      }
    } catch {
      violations.push("evidence:invalid-json");
    }
  }


  for (const [path, content] of Object.entries(files)) {
    if (content.includes("claim-verified")) {
      violations.push(`synthetic-claim-id:${path}`);
    }
    if (content.includes("/projects/profile")) {
      violations.push(`bogus-project-route:${path}`);
    }
  }

  // 5. Release Runbook Integrity
  const releaseRunbook = mustGet("docs/runbooks/release.md");
  const requiredSections = [
    "## Release Governance and Boundary",
    "## Pre-flight Checklist",
    "## Privacy & Data Classification Audit",
    "## Cost Review & Monthly Budget",
    "## End-to-End Release Smoke Execution",
    "## Release Promotion Procedure",
    "## Rollback Procedure",
  ];

  for (const sec of requiredSections) {
    if (!releaseRunbook.includes(sec)) {
      violations.push(`missing-runbook-section:${sec}`);
    }
  }

  return violations;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const root = join(dirname(fileURLToPath(import.meta.url)), "..");
  const files = loadRepositoryFiles(root);
  const violations = checkRelease(files);

  if (violations.length > 0) {
    console.error("Release check violations found:");
    for (const v of violations) {
      console.error(`  - ${v}`);
    }
    process.exit(1);
  } else {
    console.log(`Release checks passed (${Object.keys(files).length} files checked).`);
  }
}
