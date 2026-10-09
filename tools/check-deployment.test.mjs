// Proves each deployment invariant by breaking the real definitions in memory and requiring the
// matching violation. A mutation that does not change its file fails loudly, so a stale pattern
// can never turn a test into a silent pass.
//
//   node --test tools/

import assert from "node:assert/strict";
import { dirname, join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { checkDeployment, loadRepositoryFiles } from "./check-deployment.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const real = loadRepositoryFiles(root);

const GCP_MAIN = "deployment/gcp/main.tf";
const GCP_WIF = "deployment/gcp/wif.tf";
const GCP_APIS = "deployment/gcp/apis.tf";
const GCP_VARS = "deployment/gcp/variables.tf";
const CF_VARS = "deployment/cloudflare/variables.tf";
const CF_MAIN = "deployment/cloudflare/main.tf";
const CF_VERSIONS = "deployment/cloudflare/versions.tf";
const CF_TFVARS = "deployment/cloudflare/terraform.tfvars.example";
const GCP_TFVARS = "deployment/gcp/terraform.tfvars.example";
const WRANGLER = "frontend/wrangler.jsonc";
const DEPLOY = ".github/workflows/deploy.yml";
const CI = ".github/workflows/ci.yml";
const RUNBOOK = "docs/runbooks/infrastructure.md";
const ENTRY = "frontend/worker/entry.ts";
const LEDGER_MAIN = "deployment/control-ledger/main.tf";
const LEDGER_VARS = "deployment/control-ledger/variables.tf";
const LEDGER_VERSIONS = "deployment/control-ledger/versions.tf";
const MODEL_CONTROL = "docs/runbooks/model-control.md";
const STAGE_SOURCE = "frontend/src/modules/security/stage.ts";
const VISITOR_SOURCE = "frontend/src/modules/security/visitor.ts";
const IDENTITY_SOURCE = "frontend/src/modules/security/identity.ts";

function mutate(path, edit) {
  const before = real[path];
  assert.equal(typeof before, "string", `${path} was not loaded`);
  const after = edit(before);
  assert.notEqual(after, before, `the mutation did not change ${path}`);
  return { ...real, [path]: after };
}

const replace = (from, to) => (text) => text.replace(from, to);
const append = (extra) => (text) => `${text}\n${extra}\n`;

test("the real definitions satisfy every invariant", () => {
  assert.deepEqual(checkDeployment(real), []);
});

const cases = [
  // Remote runtime safety
  ["remote runtime is Development", GCP_MAIN, replace('aspnetcore_environment = "Production"', 'aspnetcore_environment = "Development"'), "runtime-environment"],
  ["runtime environment follows the stage", GCP_MAIN, replace('aspnetcore_environment = "Production"', 'aspnetcore_environment = var.environment == "prod" ? "Production" : "Development"'), "runtime-environment"],
  ["environment variable literal instead of the local", GCP_MAIN, replace("value = local.aspnetcore_environment", 'value = "Production"'), "runtime-environment"],

  // Billing and both scaling scopes
  ["request-based CPU setting omitted", GCP_MAIN, replace(/\s+cpu_idle = true\n/, "\n"), "request-billing"],
  ["continuous CPU enabled", GCP_MAIN, replace("cpu_idle = true", "cpu_idle = false"), "request-billing"],
  ["CPU setting only in a comment", GCP_MAIN, replace("cpu_idle = true", "# cpu_idle = true"), "request-billing"],
  ["CPU setting in the wrong scope", GCP_MAIN, (text) => text.replace(/\s+cpu_idle = true\n/, "\n").replace("  template {", "  cpu_idle = true\n  template {"), "request-billing"],
  ["service-level scaling omitted", GCP_MAIN, replace(/\n  scaling \{[\s\S]*?\n  \}\n/, "\n"), "service-scaling"],
  ["manual service scaling selected", GCP_MAIN, replace('"AUTOMATIC"', '"MANUAL"'), "service-scaling"],
  ["service minimum detached from stage inputs", GCP_MAIN, replace("min_instance_count = var.min_instances", "min_instance_count = 1"), "service-scaling"],
  ["revision maximum detached from stage inputs", GCP_MAIN, replace("max_instance_count = var.max_instances", "max_instance_count = 20"), "revision-scaling"],
  ["revision-level scaling omitted", GCP_MAIN, replace(/\n    scaling \{[\s\S]*?\n    \}\n/, "\n"), "revision-scaling"],

  // Secrets out of state
  ["secret version owned by Terraform", GCP_MAIN, append('resource "google_secret_manager_secret_version" "v" {\n  secret_data = "x"\n}'), "secret-state"],
  ["Turnstile widget owned by Terraform", CF_MAIN, append('resource "cloudflare_turnstile_widget" "w" {}'), "secret-state"],
  ["Worker secret binding in Terraform", CF_MAIN, append("secret_text_binding {}"), "secret-state"],
  ["proxy identity secret variable in Cloudflare", CF_VARS, append('variable "proxy_identity_secret" {}'), "secret-state"],
  ["secret output in Cloudflare", "deployment/cloudflare/outputs.tf", append('output "turnstile_secret_key" {\n  value = "x"\n}'), "secret-state"],
  ["wrangler declares vars", WRANGLER, replace('"env": {', '"vars": { "BACKEND": "x" },\n  "env": {'), "secret-state"],
  ["wrangler lists a token", WRANGLER, replace('"env": {', '"env": {\n    "ci": { "name": "x-token" },'), "secret-state"],
  ["another secret in the deploy workflow", DEPLOY, replace("NEXT_PUBLIC_TURNSTILE_SITE_KEY: ${{ vars.NEXT_PUBLIC_TURNSTILE_SITE_KEY }}", "NEXT_PUBLIC_TURNSTILE_SITE_KEY: ${{ vars.NEXT_PUBLIC_TURNSTILE_SITE_KEY }}\n          LEAK: ${{ secrets.ASSISTANT_PROXY_IDENTITY_SECRET }}"), "secret-state"],
  ["secret passed as a variable", DEPLOY, replace('--var "PORTFOLIO_BACKEND_URL:${BACKEND_URL}"', '--var "PORTFOLIO_BACKEND_URL:${BACKEND_URL}" --var ASSISTANT_PROXY_IDENTITY_SECRET:x'), "secret-state"],
  ["ci uses a secret", CI, replace("contents: read\n\njobs:", "contents: read\n\nenv:\n  T: ${{ secrets.T }}\n\njobs:"), "secret-state"],

  // Worker ownership and alignment
  ["Terraform owns the Worker script", CF_MAIN, append('resource "cloudflare_workers_script" "frontend" {}'), "release-ownership"],
  ["Worker placeholder script reintroduced", null, null, "release-ownership"],
  ["wrangler env name drifts", WRANGLER, replace('"name": "rafael-portfolio-frontend-dev"', '"name": "frontend"'), "worker-alignment"],
  ["prod served on workers.dev", WRANGLER, replace('"workers_dev": false', '"workers_dev": true'), "worker-alignment"],
  ["Terraform prefix drifts from wrangler", CF_VARS, replace('default     = "rafael-portfolio-frontend"', 'default     = "frontend"'), "worker-alignment"],
  ["Terraform stages drift from wrangler", CF_VARS, replace('["dev", "prod"]', '["dev", "staging", "prod"]'), "worker-alignment"],
  ["route targets another name", CF_MAIN, replace("script_name = local.worker_name", 'script_name = "frontend"'), "worker-alignment"],
  ["build does not select the stage", DEPLOY, replace("CLOUDFLARE_ENV: ${{ inputs.environment }}", "UNUSED: 1"), "worker-alignment"],
  ["deploy omits the runtime variable", DEPLOY, replace(' --var "PORTFOLIO_BACKEND_URL:${BACKEND_URL}"', ""), "runtime-config"],
  ["deploy omits the secret gate", DEPLOY, replace("wrangler secret list --name", "wrangler whoami --name"), "runtime-config"],

  // Disabled Contact and bootstrap
  ["service needs a contact credential", GCP_MAIN, append('resource "google_secret_manager_secret" "c" {\n  secret_id = "contact-api-token"\n}'), "contact-startup"],
  ["Contact enabled", GCP_MAIN, replace('value = "false"', 'value = "true"'), "contact-startup"],
  ["service not gated by create_service", GCP_MAIN, replace("count = var.create_service ? 1 : 0", "count = 1"), "bootstrap"],
  ["bootstrap defaults to creating the service", GCP_VARS, replace(/(variable "create_service"[\s\S]*?default\s*=\s*)false/, "$1true"), "bootstrap"],
  ["image defaults to latest", GCP_VARS, replace('default     = ""\n  nullable', 'default     = "r/p/backend:latest"\n  nullable'), "bootstrap"],
  ["latest allowed again", GCP_VARS, replace('!endswith(var.container_image, ":latest")', "true"), "bootstrap"],
  ["service created without an image", GCP_MAIN, replace('var.container_image != ""', "true"), "bootstrap"],
  ["deploy pushes latest", DEPLOY, replace('echo "reference=', 'docker push x:latest\n          echo "reference='), "release-ownership"],

  // Terraform versus deploy ownership
  ["service ignores its whole template", GCP_MAIN, replace("template[0].labels,", "template[0].labels,\n      template,"), "release-ownership"],
  ["service ignores scaling", GCP_MAIN, replace("template[0].labels,", "template[0].labels,\n      template[0].scaling,"), "release-ownership"],
  ["service ignores service-level scaling", GCP_MAIN, replace("template[0].labels,", "template[0].labels,\n      scaling,"), "release-ownership"],
  ["service ignores CPU allocation", GCP_MAIN, replace("template[0].labels,", "template[0].labels,\n      template[0].containers[0].resources[0].cpu_idle,"), "release-ownership"],
  ["service ignores everything", GCP_MAIN, replace(/ignore_changes = \[[\s\S]*?\n {4}\]/, "ignore_changes = all"), "release-ownership"],
  ["service no longer ignores the image", GCP_MAIN, replace("      template[0].containers[0].image,\n", ""), "release-ownership"],

  // Federation
  ["token credentials API missing", GCP_APIS, replace('    "iamcredentials.googleapis.com",\n', ""), "federation-apis"],
  ["security token service API missing", GCP_APIS, replace('    "sts.googleapis.com",\n', ""), "federation-apis"],
  ["resource manager API missing", GCP_APIS, replace('    "cloudresourcemanager.googleapis.com",\n', ""), "federation-apis"],
  ["pool no longer waits for the APIs", GCP_WIF, replace("\n  depends_on = [\n    google_project_service.required\n  ]", ""), "federation-apis"],
  ["deployer gains a broad role", GCP_WIF, replace("roles/run.developer", "roles/run.admin"), "deployer-scope"],
  ["project-level IAM added", GCP_WIF, append('resource "google_project_iam_member" "x" {}'), "deployer-scope"],
  ["trust ignores the environment", GCP_WIF, replace(" && assertion.environment == '${var.environment}'", ""), "federation-trust"],
  ["trust ignores the branch", GCP_WIF, replace(" && assertion.ref == '${local.trusted_ref}'", ""), "federation-trust"],
  ["prod trusts develop", GCP_WIF, replace('"refs/heads/main" : "refs/heads/develop"', '"refs/heads/develop" : "refs/heads/develop"'), "federation-trust"],

  // Workflow gating and permissions
  ["id-token granted workflow-wide", DEPLOY, replace("permissions:\n  contents: read\n\nconcurrency:", "permissions:\n  contents: read\n  id-token: write\n\nconcurrency:"), "permissions"],
  ["id-token granted to the frontend job", DEPLOY, replace(/(deploy-frontend:[\s\S]*?permissions:\n {6}contents: read)/, "$1\n      id-token: write"), "permissions"],
  ["id-token missing from the backend job", DEPLOY, replace("      id-token: write\n", ""), "permissions"],
  ["frontend deploys without validation", DEPLOY, replace("needs: [authorize, validate]\n    if: inputs.deploy_frontend", "needs: [authorize]\n    if: inputs.deploy_frontend"), "deploy-gate"],
  ["backend deploys without validation", DEPLOY, replace("needs: [authorize, validate]\n    if: inputs.deploy_backend", "needs: [authorize]\n    if: inputs.deploy_backend"), "deploy-gate"],
  ["backend does not pin the validated commit", DEPLOY, replace("uses: actions/checkout@v4\n        with:\n          ref: ${{ github.sha }}", "uses: actions/checkout@v4"), "deploy-gate"],
  ["deploy job not in a protected environment", DEPLOY, replace("environment: ${{ inputs.environment }}\n    permissions:\n      contents: read\n      id-token: write", "permissions:\n      contents: read\n      id-token: write"), "deploy-gate"],
  ["validation no longer calls CI", DEPLOY, replace("uses: ./.github/workflows/ci.yml", "uses: ./.github/workflows/other.yml"), "deploy-gate"],
  ["CI is not callable", CI, replace("  workflow_call:\n", ""), "deploy-gate"],
  ["CI skips the published integration smoke", CI, replace("Rafael.Portfolio.IntegrationTests.csproj", "Rafael.Portfolio.Other.csproj"), "deploy-gate"],
  ["CI skips the deployment invariants", CI, replace("node tools/check-deployment.mjs", "true"), "deploy-gate"],
  ["deploy runs on push", DEPLOY, replace("on:\n  workflow_dispatch:", "on:\n  push:\n    branches: [main]\n  workflow_dispatch:"), "manual-authorization"],
  ["prod allowed from develop", DEPLOY, replace("dev:refs/heads/develop|prod:refs/heads/main)", "dev:refs/heads/develop|prod:refs/heads/develop)"), "branch-target"],
  ["authorize no longer fails closed", DEPLOY, replace(/\*\)\n\s+echo "::error::Target[^\n]*\n\s+exit 1\n/, "*)\n              ;;\n"), "branch-target"],

  // Provider credentials never become plan inputs
  ["Cloudflare token variable restored", CF_VARS, append('variable "cloudflare_api_token" {\n  type = string\n}'), "plan-secrets"],
  ["Cloudflare provider carries a token", CF_VERSIONS, replace('provider "cloudflare" {}', 'provider "cloudflare" {\n  api_token = var.t\n}'), "plan-secrets"],
  ["Cloudflare token in the example variables", CF_TFVARS, append('cloudflare_api_token = "x"'), "plan-secrets"],
  ["sensitive root input in GCP", GCP_VARS, append('variable "x" {\n  type      = string\n  sensitive = true\n}'), "plan-secrets"],
  ["Google provider carries credentials", "deployment/gcp/versions.tf", replace('provider "google" {', 'provider "google" {\n  credentials = "x"'), "plan-secrets"],
  ["runbook recommends a token in a variable file", RUNBOOK, replace(/CLOUDFLARE_API_TOKEN/g, "TOKEN_VAR"), "plan-secrets"],

  // Foundation-to-service transition
  ["service can be removed silently", GCP_MAIN, replace("prevent_destroy = true", "prevent_destroy = false"), "service-removal"],
  ["deletion protection dropped", GCP_MAIN, replace("deletion_protection = true", "deletion_protection = false"), "service-removal"],
  ["service-phase inputs absent from the example", GCP_TFVARS, replace(/^# create_service\s*=.*$/m, ""), "service-persistence"],
  ["runbook passes transient overrides", RUNBOOK, append("terraform apply -var create_service=true -var container_image=x"), "service-persistence"],
  ["runbook drops the persisted var file", RUNBOOK, replace(/-var-file/g, "-vars"), "service-persistence"],
  ["CI skips the plan proofs", CI, replace("node tools/check-terraform-plans.mjs", "true"), "deploy-gate"],

  // Documentation the model depends on
  ["protected environments undocumented", RUNBOOK, replace(/required reviewers/gi, "approvers"), "documentation"],
  ["bootstrap undocumented", RUNBOOK, replace(/create_service/g, "service_flag"), "documentation"],
  ["out-of-band secrets undocumented", RUNBOOK, replace(/gcloud secrets versions add/g, "gcloud secrets add"), "documentation"],

  // One explicit stage, admission before assets, and the approved edge limits
  ["Wrangler main is not the admission entry", WRANGLER, replace('"main": "worker/entry.ts"', '"main": "vinext/server/fetch-handler"'), "stage-admission"],
  ["assets are served before the Worker runs", WRANGLER, replace('"run_worker_first": true', '"run_worker_first": false'), "stage-admission"],
  ["run_worker_first omitted", WRANGLER, replace(/,\n\s*\/\/ Without this[\s\S]*?"run_worker_first": true/, ""), "stage-admission"],
  ["dev stage variable missing", WRANGLER, replace('"PORTFOLIO_STAGE": "dev"', '"OTHER": "dev"'), "stage-admission"],
  ["prod names the wrong stage", WRANGLER, replace('"PORTFOLIO_STAGE": "prod"', '"PORTFOLIO_STAGE": "dev"'), "stage-admission"],
  ["stage variable has a non-canonical value", WRANGLER, replace('"PORTFOLIO_STAGE": "dev"', '"PORTFOLIO_STAGE": "development"'), "stage-admission"],
  ["extra plaintext variable in a stage", WRANGLER, replace('"PORTFOLIO_STAGE": "dev"', '"PORTFOLIO_STAGE": "dev",\n        "PORTFOLIO_BACKEND_URL": "https://x"'), "stage-admission"],
  ["ordinary IP limit raised", WRANGLER, replace('"namespace_id": "1101", "simple": { "limit": 5,', '"namespace_id": "1101", "simple": { "limit": 50,'), "edge-limits"],
  ["team IP limit lowered", WRANGLER, replace('"namespace_id": "1202", "simple": { "limit": 15,', '"namespace_id": "1202", "simple": { "limit": 3,'), "edge-limits"],
  ["country limit lowered", WRANGLER, replace('"namespace_id": "1103", "simple": { "limit": 100,', '"namespace_id": "1103", "simple": { "limit": 10,'), "edge-limits"],
  ["rate-limit period changed", WRANGLER, replace('"namespace_id": "1201", "simple": { "limit": 5, "period": 60 }', '"namespace_id": "1201", "simple": { "limit": 5, "period": 10 }'), "edge-limits"],
  ["country binding removed from prod", WRANGLER, replace(/\n\s*\{ "name": "RATE_LIMIT_COUNTRY", "namespace_id": "1203"[^\n]*/, ""), "edge-limits"],
  ["rate-limit bindings removed from dev", WRANGLER, replace(/,\n\s*"ratelimits": \[\n(?:\s*\{ "name": "RATE_LIMIT[^\n]*\n){3}\s*\]\n(\s*\},\n\s*"prod")/, "\n$1"), "edge-limits"],
  ["stages share rate-limit namespaces", WRANGLER, replace('"namespace_id": "1201"', '"namespace_id": "1101"'), "edge-limits"],
  ["entry dispatches without admitting", ENTRY, replace(/const decision = await admitRequest\(request, env as AdmissionEnv\);\s*if \(decision\.action === "respond"\) return decision\.response;\s*return handler\.fetch\(decision\.request, env, ctx\);/, "return handler.fetch(request, env, ctx);"), "stage-admission"],
  ["entry admits after dispatching", ENTRY, replace(/const decision = await admitRequest\(request, env as AdmissionEnv\);\s*if \(decision\.action === "respond"\) return decision\.response;\s*return handler\.fetch\(decision\.request, env, ctx\);/, "const response = await handler.fetch(request, env, ctx);\n    await admitRequest(request, env as AdmissionEnv);\n    return response;"), "stage-admission"],
  ["stage inferred from NODE_ENV", STAGE_SOURCE, append("export const inferred = process.env.NODE_ENV === \"production\" ? \"prod\" : \"dev\";"), "stage-admission"],
  ["stage inferred from an alternative variable", IDENTITY_SOURCE, append("export const alt = process.env.NEXT_PUBLIC_APP_STAGE;"), "stage-admission"],
  ["visitor read from X-Forwarded-For", VISITOR_SOURCE, append("export const fallback = (request: Request) => request.headers.get(\"x-forwarded-for\");"), "trusted-metadata"],
  ["visitor read from True-Client-IP", IDENTITY_SOURCE, append("export const tci = \"True-Client-IP\";"), "trusted-metadata"],
  ["backend stage not passed by Terraform", GCP_MAIN, replace('name  = "Portfolio__Stage"', 'name  = "Portfolio_Stage"'), "stage-admission"],
  ["backend stage hard-coded in Terraform", GCP_MAIN, replace(/(name {2}= "Portfolio__Stage"\n\s+value = )var\.environment/, '$1"prod"'), "stage-admission"],
  ["deploy skips the generated Worker check", DEPLOY, replace('node ../tools/check-wrangler-build.mjs "${TARGET}"', "true"), "stage-admission"],
  ["deploy does not require the allowlist secret", DEPLOY, replace('"ASSISTANT_PROXY_IDENTITY_SECRET", "TEAM_ALLOWLIST"', '"ASSISTANT_PROXY_IDENTITY_SECRET"'), "stage-admission"],
  ["allowlist passed to the deploy as a variable", DEPLOY, replace('--var "PORTFOLIO_BACKEND_URL:${BACKEND_URL}"', '--var "PORTFOLIO_BACKEND_URL:${BACKEND_URL}" --var TEAM_ALLOWLIST:x'), "secret-state"],
  ["runbook omits the backend stage", RUNBOOK, replace(/Portfolio__Stage/g, "BackendStage"), "documentation"],
  ["runbook omits the allowlist secret", RUNBOOK, replace(/TEAM_ALLOWLIST/g, "ALLOWED_IPS"), "documentation"],
  ["runbook overstates the edge limits", RUNBOOK, replace(/approximate/gi, "exact"), "documentation"],
  ["runbook omits admission before assets", RUNBOOK, replace(/run_worker_first/g, "assets_first"), "documentation"],

  // The shared model control database
  ["database created without activation", LEDGER_VARS, replace(/(variable "create_database"[\s\S]*?default\s*=\s*)false/, "$1true"), "model-control"],
  ["database not gated by create_database", LEDGER_MAIN, replace("count = var.create_database ? 1 : 0\n\n  project     = var.project_id\n  name ", "count = 1\n\n  project     = var.project_id\n  name "), "model-control"],
  ["database deletion protection dropped", LEDGER_MAIN, replace('delete_protection_state           = "DELETE_PROTECTION_ENABLED"', 'delete_protection_state           = "DELETE_PROTECTION_DISABLED"'), "model-control"],
  ["database no longer protected in Terraform", LEDGER_MAIN, replace("prevent_destroy = true", "prevent_destroy = false"), "model-control"],
  ["database deleted with the stack", LEDGER_MAIN, replace('deletion_policy                   = "ABANDON"', 'deletion_policy                   = "DELETE"'), "model-control"],
  ["datastore mode database", LEDGER_MAIN, replace('type        = "FIRESTORE_NATIVE"', 'type        = "DATASTORE_MODE"'), "model-control"],
  ["TTL lost on the reservations", LEDGER_MAIN, replace('toset(["model_control_periods", "model_control_reservations"])', 'toset(["model_control_periods"])'), "model-control"],
  ["TTL on the wrong field", LEDGER_MAIN, replace('field      = "expiresAt"', 'field      = "createdAt"'), "model-control"],
  ["TTL field loses its index", LEDGER_MAIN, replace("ttl_config {}", "ttl_config {}\n  index_config {}"), "model-control"],
  ["a broad role for the runtime", LEDGER_MAIN, replace('role    = "roles/datastore.user"', 'role    = "roles/datastore.owner"'), "model-control"],
  ["runtime access without the database condition", LEDGER_MAIN, replace(/\n  condition \{[\s\S]*?\n  \}\n/, "\n"), "model-control"],
  ["runtime access not gated", LEDGER_MAIN, replace("for_each = var.create_database ? toset(var.runtime_service_accounts) : toset([])", "for_each = toset(var.runtime_service_accounts)"), "model-control"],
  ["a public principal", LEDGER_MAIN, append('# allUsers\nresource "google_project_iam_member" "public" {\n  member = "allUsers"\n}'), "model-control"],
  ["a second grant resource", LEDGER_MAIN, append('resource "google_project_iam_binding" "extra" {\n  role = "roles/datastore.user"\n}'), "model-control"],
  ["a stage declares the database", GCP_MAIN, append('resource "google_firestore_database" "own" {}'), "model-control"],
  ["a stage enables the Firestore API", GCP_APIS, replace('"cloudresourcemanager.googleapis.com",', '"cloudresourcemanager.googleapis.com",\n    "firestore.googleapis.com",'), "model-control"],
  ["the stage drops the project id", GCP_MAIN, replace('"Assistant__ModelControl__Firestore__ProjectId"  = var.control_ledger_project_id', '"Assistant__ModelControl__Firestore__ProjectId"  = "other-project"'), "model-control"],
  ["the stage drops the database id", GCP_MAIN, replace('"Assistant__ModelControl__Firestore__DatabaseId" = var.control_ledger_database_id', '"Assistant__ModelControl__Firestore__DatabaseId" = "own-database"'), "model-control"],
  ["the stage allows one half of the pair", GCP_MAIN, replace('(var.control_ledger_project_id == "") == (var.control_ledger_database_id == "")', "true"), "model-control"],
  ["Terraform configures a tariff", GCP_MAIN, replace('"Assistant__ModelControl__Firestore__DatabaseId" = var.control_ledger_database_id', '"Assistant__ModelControl__Firestore__DatabaseId" = var.control_ledger_database_id\n    "Assistant__ModelControl__Tariff__Version" = "t"'), "model-control"],
  ["Terraform initializes the store", GCP_MAIN, replace('"Assistant__ModelControl__Firestore__DatabaseId" = var.control_ledger_database_id', '"Assistant__ModelControl__Firestore__DatabaseId" = var.control_ledger_database_id\n    "Assistant__ModelControl__InitializeStore" = "true"'), "model-control"],
  ["CI stops starting the emulator", CI, replace("gcloud emulators firestore start", "gcloud emulators other start"), "model-control"],
  ["CI stops pointing tests at the emulator", CI, replace("FIRESTORE_EMULATOR_HOST: 127.0.0.1:8080", "UNUSED: 1"), "model-control"],
  ["CI drops the emulator tests", CI, replace(/Rafael\.Portfolio\.EmulatorTests\.csproj/g, "Rafael.Portfolio.Other.csproj"), "model-control"],
  ["CI stops validating the control ledger", CI, replace("terraform -chdir=deployment/control-ledger validate", "true"), "deploy-gate"],
  ["a credential on the control ledger provider", LEDGER_VERSIONS, replace('provider "google" {', 'provider "google" {\n  credentials = "x"'), "plan-secrets"],
  ["a sensitive control ledger input", LEDGER_VARS, append('variable "token" {\n  type      = string\n  sensitive = true\n}'), "plan-secrets"],
  ["a secret payload in the control ledger", LEDGER_MAIN, append('resource "google_secret_manager_secret_version" "v" {\n  secret_data = "x"\n}'), "secret-state"],
  ["runbook forgets the invoice-cap limit", MODEL_CONTROL, replace(/not a total invoice cap/gi, "a cost gate"), "documentation"],
  ["runbook forgets the 40-day retention", MODEL_CONTROL, replace(/40 days/g, "some time"), "documentation"],
  ["runbook lets the emulator checks pass silently", MODEL_CONTROL, replace(/INCOMPLETE/g, "skipped"), "documentation"],
  ["runbook forgets that unknown outcomes stay charged", MODEL_CONTROL, replace(/Uncertain/g, "Unknown"), "documentation"],
  ["runbook forgets what is never stored", MODEL_CONTROL, replace(/IP address or country/gi, "visitor details"), "documentation"],
  ["runbook lets unresolved reservations expire", MODEL_CONTROL, replace(/no expiry/gi, "a short expiry"), "documentation"],
  ["runbook forgets explicit reconciliation", MODEL_CONTROL, replace(/ReconcileAsync/g, "AutoRelease"), "documentation"],
  ["runbook forgets that in-flight permits are held", MODEL_CONTROL, replace(/in\s+flight/gi, "pending"), "documentation"],
  ["runbook forgets the race-safe capacity rule", MODEL_CONTROL, replace(/ConcurrentChange/g, "Retry"), "documentation"],
  ["runbook forgets the pending call scope", MODEL_CONTROL, replace(/pending\s+call/gi, "whole turn"), "documentation"],
  ["runbook forgets that earlier usage is retained", MODEL_CONTROL, replace(/retained\s+and\s+priced/gi, "ignored"), "documentation"],
  ["runbook forgets the independent counters", MODEL_CONTROL, replace(/judged\s+independently/gi, "treated together"), "documentation"],
  ["runbook forgets the exact schema version rule", MODEL_CONTROL, replace(/schemaVersion/g, "version"), "documentation"],
  ["infrastructure runbook forgets the shared database", RUNBOOK, replace(/deployment\/control-ledger/g, "deployment/other"), "documentation"],
];

for (const [name, path, edit, id] of cases) {
  test(`detects: ${name}`, () => {
    const files = path === null ? { ...real, "deployment/cloudflare/scripts/worker_placeholder.js": "" } : mutate(path, edit);
    const violations = checkDeployment(files);
    assert.ok(
      violations.some((violation) => violation.startsWith(`[${id}]`)),
      `expected a [${id}] violation, got ${JSON.stringify(violations)}`
    );
  });
}

test("detects: application middleware returning", () => {
  const violations = checkDeployment({ ...real, "frontend/src/middleware.ts": "" });
  assert.ok(violations.some((violation) => violation.startsWith("[stage-admission]")), JSON.stringify(violations));
});

test("every case names a distinct, non-empty mutation", () => {
  const names = cases.map(([name]) => name);
  assert.equal(new Set(names).size, names.length);
});

test("service and revision scaling are recognized independently of block order", () => {
  const files = mutate(GCP_MAIN, (text) => {
    const block = /\n  scaling \{[\s\S]*?\n  \}\n/.exec(text)?.[0];
    assert.ok(block);
    return text.replace(block, "\n").replace("  lifecycle {", `${block}\n  lifecycle {`);
  });
  assert.deepEqual(checkDeployment(files), []);
});
