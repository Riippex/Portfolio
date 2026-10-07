#!/usr/bin/env node
// Offline regression checks for the deployment definitions (Terraform, Wrangler, GitHub
// workflows). It reads files only: no network, no provider state, no credentials.
//
//   node tools/check-deployment.mjs          check the repository, exit 1 on a violation
//
// checkDeployment(files) takes a { relativePath: contents } map and returns violation
// strings, so tools/check-deployment.test.mjs can prove each invariant by breaking it.

import { existsSync, readdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

export const CHECKED_FILES = [
  "deployment/gcp/apis.tf",
  "deployment/gcp/main.tf",
  "deployment/gcp/outputs.tf",
  "deployment/gcp/variables.tf",
  "deployment/gcp/versions.tf",
  "deployment/gcp/wif.tf",
  "deployment/gcp/terraform.tfvars.example",
  "deployment/cloudflare/main.tf",
  "deployment/cloudflare/outputs.tf",
  "deployment/cloudflare/variables.tf",
  "deployment/cloudflare/versions.tf",
  "deployment/cloudflare/terraform.tfvars.example",
  "frontend/wrangler.jsonc",
  ".github/workflows/ci.yml",
  ".github/workflows/deploy.yml",
  "docs/runbooks/infrastructure.md",
  "deployment/README.md",
];

const REQUIRED_APIS = [
  "run.googleapis.com",
  "artifactregistry.googleapis.com",
  "secretmanager.googleapis.com",
  "iam.googleapis.com",
  "iamcredentials.googleapis.com",
  "sts.googleapis.com",
  "cloudresourcemanager.googleapis.com",
];

const ALLOWED_SERVICE_IGNORES = [
  "client",
  "client_version",
  "labels",
  "template[0].labels",
  "template[0].containers[0].image",
];

const ALLOWED_DEPLOYER_ROLES = [
  "roles/artifactregistry.writer",
  "roles/iam.serviceAccountUser",
  "roles/iam.workloadIdentityUser",
  "roles/run.developer",
];

// ---------------------------------------------------------------------------------------
// Parsing helpers
// ---------------------------------------------------------------------------------------

function stripHcl(text) {
  return text
    .replace(/\/\*[\s\S]*?\*\//g, "")
    .split("\n")
    .filter((line) => !/^\s*(#|\/\/)/.test(line))
    .join("\n");
}

function stripYaml(text) {
  return text
    .split("\n")
    .filter((line) => !/^\s*#/.test(line))
    .join("\n");
}

// Removes // and /* */ comments from JSONC while leaving string contents alone.
function parseJsonc(text) {
  let out = "";
  let inString = false;
  for (let i = 0; i < text.length; i += 1) {
    const ch = text[i];
    const next = text[i + 1];
    if (inString) {
      out += ch;
      if (ch === "\\") {
        out += next ?? "";
        i += 1;
      } else if (ch === '"') {
        inString = false;
      }
    } else if (ch === '"') {
      inString = true;
      out += ch;
    } else if (ch === "/" && next === "/") {
      while (i < text.length && text[i] !== "\n") i += 1;
      out += "\n";
    } else if (ch === "/" && next === "*") {
      i += 2;
      while (i < text.length && !(text[i] === "*" && text[i + 1] === "/")) i += 1;
      i += 1;
    } else {
      out += ch;
    }
  }
  return JSON.parse(out.replace(/,(\s*[}\]])/g, "$1"));
}

// Returns the body of the first `<header> { ... }` block, balancing braces.
function hclBlock(text, header) {
  const start = text.search(header);
  if (start < 0) return null;
  const open = text.indexOf("{", start);
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

// Returns the text inside the first [ ... ] that follows `header`, balancing nested brackets
// (so `template[0].containers[0].image` stays whole).
function bracketList(text, header) {
  const start = text.search(header);
  if (start < 0) return null;
  const open = text.indexOf("[", start);
  if (open < 0) return null;
  let depth = 0;
  for (let i = open; i < text.length; i += 1) {
    if (text[i] === "[") depth += 1;
    else if (text[i] === "]") {
      depth -= 1;
      if (depth === 0) return text.slice(open + 1, i);
    }
  }
  return null;
}

function resourceBlock(text, type, name) {
  return hclBlock(text, new RegExp(`resource\\s+"${type}"\\s+"${name}"`));
}

function listItems(body) {
  return body
    .split(/,|\n/)
    .map((item) => item.trim())
    .filter(Boolean);
}

// Splits a workflow into its top-level jobs: { name: text }.
function yamlJobs(text) {
  const lines = text.split("\n");
  const jobsAt = lines.findIndex((line) => /^jobs:\s*$/.test(line));
  const jobs = {};
  if (jobsAt < 0) return jobs;
  let current = null;
  for (const line of lines.slice(jobsAt + 1)) {
    const header = /^ {2}([A-Za-z0-9_-]+):\s*$/.exec(line);
    if (header) {
      current = header[1];
      jobs[current] = "";
    } else if (current !== null) {
      jobs[current] += `${line}\n`;
    }
  }
  return jobs;
}

// Returns the indented block that follows `key:` at the given indent, or null.
function yamlBlock(text, key, indent) {
  const lines = text.split("\n");
  const pad = " ".repeat(indent);
  const at = lines.findIndex((line) => line === `${pad}${key}:` || line.startsWith(`${pad}${key}: `));
  if (at < 0) return null;
  const inline = lines[at].slice(pad.length + key.length + 1).trim();
  const body = [];
  for (const line of lines.slice(at + 1)) {
    if (line.trim() === "" || line.startsWith(`${pad} `)) body.push(line);
    else break;
  }
  return { inline, body: body.join("\n") };
}

function needsOf(jobText) {
  const block = yamlBlock(jobText, "needs", 4);
  if (!block) return [];
  const raw = block.inline || block.body;
  return raw.replace(/[[\]\-]/g, " ").split(/[\s,]+/).filter(Boolean);
}

// ---------------------------------------------------------------------------------------
// The checks
// ---------------------------------------------------------------------------------------

export function checkDeployment(files) {
  const violations = [];
  const fail = (id, message) => violations.push(`[${id}] ${message}`);
  const need = (path) => {
    if (typeof files[path] !== "string") {
      fail("files", `missing ${path}`);
      return "";
    }
    return files[path];
  };

  const gcp = {
    main: stripHcl(need("deployment/gcp/main.tf")),
    wif: stripHcl(need("deployment/gcp/wif.tf")),
    apis: stripHcl(need("deployment/gcp/apis.tf")),
    variables: stripHcl(need("deployment/gcp/variables.tf")),
    outputs: stripHcl(need("deployment/gcp/outputs.tf")),
    versions: stripHcl(need("deployment/gcp/versions.tf")),
  };
  const gcpAll = Object.values(gcp).join("\n");
  const gcpTfvars = need("deployment/gcp/terraform.tfvars.example");

  const cf = {
    main: stripHcl(need("deployment/cloudflare/main.tf")),
    variables: stripHcl(need("deployment/cloudflare/variables.tf")),
    outputs: stripHcl(need("deployment/cloudflare/outputs.tf")),
    versions: stripHcl(need("deployment/cloudflare/versions.tf")),
  };
  const cfAll = Object.values(cf).join("\n");
  const cfTfvars = need("deployment/cloudflare/terraform.tfvars.example");

  const ci = stripYaml(need(".github/workflows/ci.yml"));
  const deploy = stripYaml(need(".github/workflows/deploy.yml"));
  const deployJobs = yamlJobs(deploy);

  let wrangler = {};
  try {
    wrangler = parseJsonc(need("frontend/wrangler.jsonc"));
  } catch (error) {
    fail("wrangler", `frontend/wrangler.jsonc does not parse: ${error.message}`);
  }

  // 1. Remote runtime safety ------------------------------------------------------------
  const runtimeEnv = /aspnetcore_environment\s*=\s*"([^"]*)"/.exec(gcp.main)?.[1];
  if (runtimeEnv !== "Production") {
    fail("runtime-environment", `the Cloud Run ASP.NET environment must be "Production", found ${JSON.stringify(runtimeEnv)}`);
  }
  if (/aspnetcore_environment\s*=[^\n]*var\./.test(gcp.main)) {
    fail("runtime-environment", "the ASP.NET environment must not be derived from a variable such as the deployment stage");
  }
  const envBlock = /name\s*=\s*"ASPNETCORE_ENVIRONMENT"\s*\n\s*value\s*=\s*([^\n]+)/.exec(gcp.main)?.[1].trim();
  if (envBlock !== "local.aspnetcore_environment") {
    fail("runtime-environment", "ASPNETCORE_ENVIRONMENT must be set from local.aspnetcore_environment");
  }
  if (/Development/.test(gcpAll)) {
    fail("runtime-environment", "no GCP definition may reference the Development environment");
  }
  if (!/validation\s*\{[\s\S]*?dev[\s\S]*?prod[\s\S]*?\}/.test(hclBlock(gcp.variables, /variable\s+"environment"/) ?? "")) {
    fail("runtime-environment", "the stage variable must stay a validated stage name");
  }

  // 2. Secrets stay out of Terraform state ----------------------------------------------
  for (const [label, text] of [["GCP", gcpAll], ["Cloudflare", cfAll]]) {
    if (/secret_data|secret_data_wo|google_secret_manager_secret_version|secret_text_binding|cloudflare_turnstile_widget|random_password|google_service_account_key|private_key/.test(text)) {
      fail("secret-state", `${label} definitions must not own secret payloads (secret versions, secret bindings, the Turnstile widget, generated secrets, or keys)`);
    }
  }
  if (/proxy_identity_secret|ASSISTANT_PROXY_IDENTITY_SECRET/.test(cfAll)) {
    fail("secret-state", "the Cloudflare stack must not own the proxy identity secret");
  }
  if (/\.secret\b/.test(cfAll) || /output\s+"[^"]*secret[^"]*"/i.test(cfAll)) {
    fail("secret-state", "the Cloudflare stack must not reference or export a secret value");
  }
  if (/cloudflare_workers_script|cloudflare_worker\b/.test(cfAll)) {
    fail("release-ownership", "Terraform must not own the Worker script: a later apply could replace the released code");
  }
  if (Object.keys(files).some((path) => path.startsWith("deployment/cloudflare/scripts/"))) {
    fail("release-ownership", "the placeholder Worker script must not exist");
  }

  // 3. Names, routes, and runtime configuration are aligned -----------------------------
  const prefix = /variable\s+"worker_name_prefix"[\s\S]*?default\s*=\s*"([^"]+)"/.exec(cf.variables)?.[1];
  const stages = (/contains\(\[([^\]]+)\],\s*var\.environment\)/.exec(cf.variables)?.[1] ?? "")
    .split(",")
    .map((item) => item.trim().replace(/"/g, ""))
    .filter(Boolean);
  if (!prefix || prefix !== wrangler.name) {
    fail("worker-alignment", `Terraform worker_name_prefix (${prefix}) must equal the Wrangler name (${wrangler.name})`);
  }
  if (!/worker_name\s*=\s*"\$\{var\.worker_name_prefix\}-\$\{var\.environment\}"/.test(cf.main)) {
    fail("worker-alignment", 'the Terraform Worker name must be "${var.worker_name_prefix}-${var.environment}"');
  }
  if (!/script_name\s*=\s*local\.worker_name/.test(cf.main)) {
    fail("worker-alignment", "the Worker route must target local.worker_name");
  }
  const wranglerEnvs = Object.keys(wrangler.env ?? {});
  const workflowStages = (/options:\s*\n((?:\s+-\s+\S+\n)+)/.exec(deploy)?.[1] ?? "")
    .split("\n")
    .map((line) => line.replace(/^\s*-\s*/, "").trim())
    .filter(Boolean);
  const sameSet = (a, b) => a.length === b.length && [...a].sort().join() === [...b].sort().join();
  if (!sameSet(wranglerEnvs, stages) || !sameSet(wranglerEnvs, workflowStages)) {
    fail("worker-alignment", `stages must agree: Wrangler [${wranglerEnvs}], Terraform [${stages}], workflow [${workflowStages}]`);
  }
  for (const stage of wranglerEnvs) {
    if (wrangler.env[stage]?.name !== `${wrangler.name}-${stage}`) {
      fail("worker-alignment", `Wrangler env ${stage} must be named ${wrangler.name}-${stage}`);
    }
  }
  if (wrangler.env?.prod?.workers_dev !== false) {
    fail("worker-alignment", "prod must be served only through the Terraform-owned route (workers_dev false)");
  }
  if (wrangler.vars && Object.keys(wrangler.vars).length > 0) {
    fail("secret-state", "wrangler.jsonc must not declare vars: runtime values come from the deploy command and secrets from the console");
  }
  if (/SECRET|TOKEN|KEY/i.test(JSON.stringify(wrangler.env ?? {}) + JSON.stringify(wrangler.vars ?? {}))) {
    fail("secret-state", "wrangler.jsonc must not list secrets or tokens");
  }

  const frontendJob = deployJobs["deploy-frontend"] ?? "";
  if (!/CLOUDFLARE_ENV:\s*\$\{\{\s*inputs\.environment\s*\}\}/.test(frontendJob)) {
    fail("worker-alignment", "the frontend build must set CLOUDFLARE_ENV from the chosen target so the Worker name is fixed per stage");
  }
  if (!/wrangler deploy --env "\$\{TARGET\}" --var "PORTFOLIO_BACKEND_URL:\$\{BACKEND_URL\}"/.test(frontendJob)) {
    fail("runtime-config", "wrangler deploy must pass --env and the PORTFOLIO_BACKEND_URL runtime variable");
  }
  if (!/ASSISTANT_PROXY_IDENTITY_SECRET/.test(frontendJob) || !/wrangler secret list --name/.test(frontendJob)) {
    fail("runtime-config", "the frontend deploy must require ASSISTANT_PROXY_IDENTITY_SECRET on the exact target Worker");
  }
  if (/--var\s+"?ASSISTANT|ASSISTANT_PROXY_IDENTITY_SECRET\s*:\s*\$/.test(deploy)) {
    fail("secret-state", "the proxy identity secret must never be passed as a variable");
  }
  if (/secret list[^\n]*--format\s+text|echo[^\n]*SECRET/i.test(deploy)) {
    fail("secret-state", "deploy steps must not print secrets");
  }

  // 4. Disabled Contact does not need credentials; staged bootstrap ----------------------
  if (!/name\s*=\s*"Contact__Enabled"\s*\n\s*value\s*=\s*"false"/.test(gcp.main)) {
    fail("contact-startup", "Contact__Enabled must be explicitly false on the service");
  }
  if (/Contact__|contact[-_]api[-_]token|contact_token/i.test(gcp.main.replace(/name\s*=\s*"Contact__Enabled"/, ""))) {
    fail("contact-startup", "the service must not depend on contact credentials while Contact is disabled");
  }
  if (/contact/i.test(gcp.outputs)) {
    fail("contact-startup", "no contact secret may be exported or declared");
  }
  if (!/variable\s+"create_service"[\s\S]*?default\s*=\s*false/.test(gcp.variables)) {
    fail("bootstrap", "create_service must default to false so the first apply cannot require a service");
  }
  const serviceBlock = resourceBlock(gcp.main, "google_cloud_run_v2_service", "backend") ?? "";
  for (const [label, block] of [
    ["the Cloud Run service", serviceBlock],
    ["the public invoker", resourceBlock(gcp.main, "google_cloud_run_v2_service_iam_member", "public_invoker") ?? ""],
    ["the CI service role", resourceBlock(gcp.wif, "google_cloud_run_v2_service_iam_member", "ci_service_deployer") ?? ""],
  ]) {
    if (!/count\s*=\s*var\.create_service/.test(block)) {
      fail("bootstrap", `${label} must be created only when create_service is true`);
    }
  }
  const imageVariable = hclBlock(gcp.variables, /variable\s+"container_image"/) ?? "";
  if (/default\s*=\s*"[^"]*[:/][^"]*"/.test(imageVariable) || !/endswith\(var\.container_image,\s*":latest"\)/.test(imageVariable)) {
    fail("bootstrap", "container_image must have no registry default and must reject :latest");
  }
  if (!/precondition\s*\{[\s\S]*?var\.container_image\s*!=\s*""/.test(serviceBlock)) {
    fail("bootstrap", "creating the service must require an existing image");
  }
  if (/:latest\b/.test(gcpTfvars.replace(/^\s*#.*$/gm, "")) || /:latest\b/.test(deploy)) {
    fail("release-ownership", "no mutable :latest image tag may be configured or pushed");
  }

  // 5. Terraform versus deploy ownership of the release image ---------------------------
  const ignoreBody = bracketList(serviceBlock, /ignore_changes\s*=/);
  if (ignoreBody === null) {
    fail("release-ownership", "the Cloud Run service must ignore the deploy-owned image");
  } else {
    const ignored = listItems(ignoreBody);
    if (!ignored.includes("template[0].containers[0].image")) {
      fail("release-ownership", "the service must ignore the container image so a later apply cannot reset a release");
    }
    const extra = ignored.filter((item) => !ALLOWED_SERVICE_IGNORES.includes(item));
    if (extra.length > 0) {
      fail("release-ownership", `the service ignores too much (${extra.join(", ")}); security, scaling, environment, and IAM must stay managed`);
    }
  }

  // 6. Federation prerequisites and keyless, resource-scoped permissions ----------------
  const services = bracketList(gcp.apis, /required_services\s*=\s*toset\(/) ?? "";
  for (const api of REQUIRED_APIS) {
    if (!services.includes(`"${api}"`)) fail("federation-apis", `${api} must be declared`);
  }
  if (!/resource\s+"google_project_service"\s+"required"[\s\S]*?for_each\s*=\s*local\.required_services/.test(gcp.apis)) {
    fail("federation-apis", "the required APIs must be enabled through google_project_service.required");
  }
  for (const [type, name] of [
    ["google_iam_workload_identity_pool", "github_pool"],
    ["google_service_account", "ci_deployer"],
    ["google_service_account_iam_member", "workload_identity_user"],
  ]) {
    if (!/depends_on\s*=\s*\[[^\]]*google_project_service\.required/.test(resourceBlock(gcp.wif, type, name) ?? "")) {
      fail("federation-apis", `${type}.${name} must depend on the required APIs`);
    }
  }
  if (!/depends_on\s*=\s*\[[^\]]*google_project_service\.required/.test(resourceBlock(gcp.main, "google_service_account", "backend") ?? "")) {
    fail("federation-apis", "the runtime service account must depend on the required APIs");
  }
  const roles = [...gcp.wif.matchAll(/role\s*=\s*"([^"]+)"/g)].map((match) => match[1]);
  const unexpected = roles.filter((role) => !ALLOWED_DEPLOYER_ROLES.includes(role));
  if (unexpected.length > 0 || roles.length !== ALLOWED_DEPLOYER_ROLES.length) {
    fail("deployer-scope", `the deployer must hold exactly ${ALLOWED_DEPLOYER_ROLES.join(", ")}; found ${roles.join(", ")}`);
  }
  if (/google_project_iam_(member|binding|policy)|google_organization_iam|google_folder_iam|credentials_json/.test(gcpAll + deploy)) {
    fail("deployer-scope", "deployer permissions must stay resource-scoped and keyless");
  }
  const condition = /attribute_condition\s*=\s*"([^"]+)"/.exec(gcp.wif)?.[1] ?? "";
  for (const needle of ["assertion.repository ==", "assertion.environment ==", "assertion.ref =="]) {
    if (!condition.includes(needle)) fail("federation-trust", `the OIDC trust condition must check ${needle}`);
  }
  if (!/trusted_ref\s*=\s*var\.environment == "prod" \? "refs\/heads\/main" : "refs\/heads\/develop"/.test(gcp.wif)) {
    fail("federation-trust", "prod must trust main and every other stage must trust develop");
  }

  // 7. Deployment gating, branches, and permissions -------------------------------------
  const triggers = yamlBlock(deploy, "on", 0)?.body ?? "";
  const triggerNames = [...triggers.matchAll(/^ {2}([a-z_]+):/gm)].map((match) => match[1]);
  if (triggerNames.length !== 1 || triggerNames[0] !== "workflow_dispatch") {
    fail("manual-authorization", `deploy must run only on workflow_dispatch, found [${triggerNames}]`);
  }
  const topPermissions = yamlBlock(deploy, "permissions", 0)?.body ?? "";
  if (/id-token/.test(topPermissions) || !/contents:\s*read/.test(topPermissions)) {
    fail("permissions", "workflow-level permissions must be contents: read without id-token");
  }
  const idTokenJobs = Object.entries(deployJobs)
    .filter(([, text]) => /id-token:\s*write/.test(text))
    .map(([name]) => name);
  if (idTokenJobs.length !== 1 || idTokenJobs[0] !== "deploy-backend") {
    fail("permissions", `id-token: write is allowed only on deploy-backend, found on [${idTokenJobs}]`);
  }
  for (const name of ["deploy-backend", "deploy-frontend"]) {
    const job = deployJobs[name] ?? "";
    const needs = needsOf(job);
    if (!needs.includes("authorize") || !needs.includes("validate")) {
      fail("deploy-gate", `${name} must need both authorize and validate`);
    }
    if (!/environment:\s*\$\{\{\s*inputs\.environment\s*\}\}/.test(job)) {
      fail("deploy-gate", `${name} must run in the GitHub environment of the chosen target`);
    }
    if (!/uses:\s*actions\/checkout@v4\s*\n\s*with:\s*\n\s*ref:\s*\$\{\{\s*github\.sha\s*\}\}/.test(job)) {
      fail("deploy-gate", `${name} must check out the exact validated commit (github.sha)`);
    }
  }
  const validateJob = deployJobs.validate ?? "";
  if (!/uses:\s*\.\/\.github\/workflows\/ci\.yml/.test(validateJob) || !needsOf(validateJob).includes("authorize")) {
    fail("deploy-gate", "validate must call the CI workflow at this commit after authorize");
  }
  if (!/^ {2}workflow_call:/m.test(yamlBlock(ci, "on", 0)?.body ?? "")) {
    fail("deploy-gate", "ci.yml must be callable (workflow_call)");
  }
  if (!/Rafael\.Portfolio\.IntegrationTests\.csproj/.test(ci)) {
    fail("deploy-gate", "ci.yml must run the published-backend integration smoke");
  }
  if (!/node tools\/check-deployment\.mjs/.test(ci)) {
    fail("deploy-gate", "ci.yml must run the deployment invariant checks");
  }
  const authorize = (/case "\$\{TARGET\}:\$\{REF\}" in([\s\S]*?)esac/.exec(deployJobs.authorize ?? "")?.[1]) ?? "";
  const pairings = [...authorize.matchAll(/^\s*([a-z]+):(refs\/heads\/[a-z]+)(?:\|([a-z]+):(refs\/heads\/[a-z]+))?\)/gm)];
  const allowed = pairings.flatMap((match) => [[match[1], match[2]], match[3] ? [match[3], match[4]] : null]).filter(Boolean).map((pair) => pair.join(":"));
  if (allowed.sort().join() !== "dev:refs/heads/develop,prod:refs/heads/main") {
    fail("branch-target", `only dev from develop and prod from main may deploy, found [${allowed}]`);
  }
  if (!/\*\)[\s\S]*?exit 1/.test(authorize)) {
    fail("branch-target", "authorize must fail closed for any other pairing");
  }
  const secretsUsed = [...deploy.matchAll(/secrets\.([A-Z0-9_]+)/g)].map((match) => match[1]);
  if (secretsUsed.some((name) => name !== "CLOUDFLARE_API_TOKEN")) {
    fail("secret-state", `deploy may use only secrets.CLOUDFLARE_API_TOKEN, found ${[...new Set(secretsUsed)]}`);
  }
  const buildStep = /name: Build Vinext bundle[\s\S]*?(?=\n {6}- name:)/.exec(frontendJob)?.[0] ?? "";
  if (!buildStep || /secrets\./.test(buildStep)) {
    fail("secret-state", "the frontend build step must exist and receive no secrets");
  }
  if (/secrets\./.test(ci)) {
    fail("secret-state", "ci.yml must not use secrets");
  }

  // 8. Provider credentials never become plan inputs --------------------------------------
  // A saved plan records root variable values, sensitive or not. Credentials therefore come
  // from the operator's environment (CLOUDFLARE_API_TOKEN, application default credentials)
  // and no root variable or provider argument may carry one.
  const providerBlock = (text, name) => hclBlock(text, new RegExp(`provider\\s+"${name}"`));
  const cloudflareProvider = providerBlock(cf.versions, "cloudflare");
  if (cloudflareProvider === null || cloudflareProvider.trim() !== "") {
    fail("plan-secrets", 'provider "cloudflare" must be empty so it authenticates from the environment');
  }
  const googleProvider = providerBlock(gcp.versions, "google") ?? "";
  if (/credentials|access_token|impersonate_service_account|private_key/.test(googleProvider)) {
    fail("plan-secrets", 'provider "google" must not carry credentials');
  }
  for (const [label, variables] of [["Cloudflare", cf.variables], ["GCP", gcp.variables]]) {
    if (/sensitive\s*=\s*true/.test(variables) || /variable\s+"[^"]*(token|secret|password|credential|api_key)[^"]*"/i.test(variables)) {
      fail("plan-secrets", `${label} must have no credential-like or sensitive root variable (saved plans record root inputs)`);
    }
  }
  if (/api_token|cloudflare_api_token/.test(cfAll + cfTfvars)) {
    fail("plan-secrets", "no Cloudflare API token input may exist in the stack or its example variables");
  }

  // 9. The foundation-to-service transition is persisted and fails closed ---------------
  if (!/prevent_destroy\s*=\s*true/.test(serviceBlock)) {
    fail("service-removal", "the Cloud Run service must set lifecycle prevent_destroy so losing its inputs cannot schedule removal");
  }
  if (!/deletion_protection\s*=\s*true/.test(serviceBlock)) {
    fail("service-removal", "the Cloud Run service must set deletion_protection explicitly");
  }
  const tfvarsLines = gcpTfvars.split("\n");
  // The service-phase block is commented out in the example (the safe default stays false); both
  // of its lines must be there, with the instruction to keep them for every later plan.
  const hasServiceLine = (pattern) => tfvarsLines.some((line) => pattern.test(line));
  if (
    !hasServiceLine(/^#\s*create_service\s*=\s*true\b/) ||
    !hasServiceLine(/^#\s*container_image\s*=\s*"/) ||
    !/KEEP them/.test(gcpTfvars) ||
    !/^create_service\s*=\s*false\b/m.test(gcpTfvars)
  ) {
    fail("service-persistence", "terraform.tfvars.example must default create_service to false and show the service-phase inputs to keep for every plan");
  }
  const runbookText = need("docs/runbooks/infrastructure.md");
  if (/-var[ =]+"?(create_service|container_image)/.test(runbookText)) {
    fail("service-persistence", "the runbook must not pass the service-phase inputs as transient -var overrides");
  }
  if (!/-var-file/.test(runbookText) || !/prevent_destroy/.test(runbookText)) {
    fail("service-persistence", "the runbook must use a persisted -var-file for every plan and explain the prevent_destroy safeguard");
  }
  if (!/CLOUDFLARE_API_TOKEN/.test(runbookText) || /\bcloudflare_api_token\s*=/.test(runbookText)) {
    fail("plan-secrets", "the runbook must authenticate Cloudflare through the CLOUDFLARE_API_TOKEN environment variable, not a variable file");
  }
  if (!/node tools\/check-terraform-plans\.mjs/.test(ci)) {
    fail("deploy-gate", "ci.yml must run the offline Terraform plan proofs");
  }

  // Documentation must state the operating model it relies on ---------------------------
  const runbook = need("docs/runbooks/infrastructure.md");
  for (const [needle, why] of [
    [/create_service/, "the staged bootstrap"],
    [/gcloud secrets versions add/, "out-of-band secret population"],
    [/wrangler secret put/, "the Worker secret"],
    [/required reviewers/i, "protected environments"],
    [/deployment branches/i, "the protected-environment branch rule"],
    [/Turnstile widget[^\n]*console|console[^\n]*Turnstile widget/i, "the console-managed Turnstile widget"],
  ]) {
    if (!needle.test(runbook)) fail("documentation", `docs/runbooks/infrastructure.md must document ${why}`);
  }

  return violations;
}

// ---------------------------------------------------------------------------------------
// CLI
// ---------------------------------------------------------------------------------------

export function loadRepositoryFiles(root) {
  const files = {};
  for (const path of CHECKED_FILES) {
    try {
      files[path] = readFileSync(join(root, path), "utf8");
    } catch {
      // reported as a missing file by checkDeployment
    }
  }
  // Anything left in the retired placeholder directory must be noticed.
  const scripts = join(root, "deployment/cloudflare/scripts");
  if (existsSync(scripts)) {
    for (const entry of readdirSync(scripts)) {
      files[`deployment/cloudflare/scripts/${entry}`] = "";
    }
  }
  return files;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const root = join(dirname(fileURLToPath(import.meta.url)), "..");
  const violations = checkDeployment(loadRepositoryFiles(root));
  if (violations.length > 0) {
    console.error(`Deployment invariants violated (${violations.length}):`);
    for (const violation of violations) console.error(`  ${violation}`);
    process.exit(1);
  }
  console.log(`Deployment invariants hold (${CHECKED_FILES.length} files checked).`);
}
