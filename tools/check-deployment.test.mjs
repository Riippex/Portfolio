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

test("every case names a distinct, non-empty mutation", () => {
  const names = cases.map(([name]) => name);
  assert.equal(new Set(names).size, names.length);
});
