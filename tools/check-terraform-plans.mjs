#!/usr/bin/env node
// Offline behavioural proofs for the Terraform stacks. It runs `terraform plan` only: never
// apply, never a real credential, never a provider API.
//
//   terraform -chdir=deployment/gcp init -backend=false
//   terraform -chdir=deployment/cloudflare init -backend=false
//   node tools/check-terraform-plans.mjs
//
// What keeps these plans offline and harmless:
//   * credentials are synthetic strings generated here;
//   * every plan uses -refresh=false and a hand-built state, so providers only compare
//     configuration with that state;
//   * HTTP(S)_PROXY points at a closed port, so any attempt to reach a provider fails loudly
//     and is reported;
//   * saved plans are written outside the repository and deleted afterwards.
//
// It proves:
//   1. A Cloudflare credential supplied through the environment never appears in a saved plan
//      (binary, decompressed entries, JSON, or text), and the scanner is shown to detect a
//      leak when a credential is passed as a root variable.
//   2. The GCP foundation-to-service transition: the fresh default creates no service, the
//      persisted service-phase inputs keep an established service, and losing them fails
//      closed instead of scheduling the service for removal.

import { spawnSync } from "node:child_process";
import { randomBytes, randomUUID } from "node:crypto";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { inflateRawSync } from "node:zlib";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const gcpDir = join(root, "deployment/gcp");
const cloudflareDir = join(root, "deployment/cloudflare");
const IMAGE = "us-central1-docker.pkg.dev/synthetic-project/portfolio/backend:synthetic-tag";

// Synthetic and unique per run, so a hit can only come from this run's input.
const syntheticToken = `SYNTH${randomBytes(18).toString("hex")}`.slice(0, 40);

const OFFLINE_ENV = {
  ...process.env,
  CLOUDFLARE_API_TOKEN: syntheticToken,
  // Hostile case: if anyone re-adds a root variable with this name, the credential would flow
  // into the plan, and the scan below must catch it. Undeclared variables ignore it.
  TF_VAR_cloudflare_api_token: syntheticToken,
  GOOGLE_OAUTH_ACCESS_TOKEN: "synthetic-access-token",
  HTTPS_PROXY: "http://127.0.0.1:9",
  HTTP_PROXY: "http://127.0.0.1:9",
  NO_PROXY: "",
  TF_IN_AUTOMATION: "1",
  CHECKPOINT_DISABLE: "1",
};

const failures = [];
const record = (ok, name, detail = "") => {
  console.log(`${ok ? "ok  " : "FAIL"} ${name}${detail ? ` (${detail})` : ""}`);
  if (!ok) failures.push(name);
};

function terraform(chdir, args, env = OFFLINE_ENV) {
  const result = spawnSync("terraform", chdir ? [`-chdir=${chdir}`, ...args] : args, {
    env,
    encoding: "utf8",
    maxBuffer: 128 * 1024 * 1024,
  });
  return { status: result.status, text: `${result.stdout ?? ""}${result.stderr ?? ""}`, stdout: result.stdout ?? "" };
}

const reachedNetwork = (text) => /proxyconnect|dial tcp|connection refused|no such host|i\/o timeout/i.test(text);

// ---------------------------------------------------------------------------------------
// Zip reading (a saved plan is a zip archive; entries are usually deflated)
// ---------------------------------------------------------------------------------------

function zipEntries(buffer) {
  const entries = [];
  let end = -1;
  for (let i = buffer.length - 22; i >= 0; i -= 1) {
    if (buffer.readUInt32LE(i) === 0x06054b50) {
      end = i;
      break;
    }
  }
  if (end < 0) return entries;
  const count = buffer.readUInt16LE(end + 10);
  let offset = buffer.readUInt32LE(end + 16);
  for (let n = 0; n < count; n += 1) {
    const method = buffer.readUInt16LE(offset + 10);
    const compressedSize = buffer.readUInt32LE(offset + 20);
    const nameLength = buffer.readUInt16LE(offset + 28);
    const extraLength = buffer.readUInt16LE(offset + 30);
    const commentLength = buffer.readUInt16LE(offset + 32);
    const localOffset = buffer.readUInt32LE(offset + 42);
    const name = buffer.toString("utf8", offset + 46, offset + 46 + nameLength);
    const localNameLength = buffer.readUInt16LE(localOffset + 26);
    const localExtraLength = buffer.readUInt16LE(localOffset + 28);
    const start = localOffset + 30 + localNameLength + localExtraLength;
    const raw = buffer.subarray(start, start + compressedSize);
    entries.push({ name, data: method === 8 ? inflateRawSync(raw) : raw });
    offset += 46 + nameLength + extraLength + commentLength;
  }
  return entries;
}

// Looks for a secret in every representation of a saved plan. Returns where it was found.
function scanPlan(chdir, planPath, secret) {
  const needles = [secret, secret.slice(0, 20), secret.slice(-20)];
  const hits = [];
  const contains = (label, data) => {
    const text = Buffer.isBuffer(data) ? data.toString("latin1") : data;
    if (needles.some((needle) => text.includes(needle))) hits.push(label);
  };

  const raw = readFileSync(planPath);
  contains("plan file bytes", raw);
  const entries = zipEntries(raw);
  for (const entry of entries) contains(`plan entry ${entry.name}`, entry.data);
  contains("terraform show -json", terraform(chdir, ["show", "-json", planPath]).stdout);
  contains("terraform show", terraform(chdir, ["show", "-no-color", planPath]).stdout);
  return { hits, entries: entries.length };
}

// ---------------------------------------------------------------------------------------
// 1. Provider credentials stay out of saved plans
// ---------------------------------------------------------------------------------------

function proveCredentialSecrecy(work) {
  // Positive control: a credential passed as a (sensitive) root variable DOES reach the saved
  // plan. This shows the scanner would notice a leak, so the clean result below means something.
  const fixture = join(work, "control");
  rmSync(fixture, { recursive: true, force: true });
  mkdirSync(fixture, { recursive: true });
  writeFileSync(
    join(fixture, "main.tf"),
    'variable "credential" {\n  type      = string\n  sensitive = true\n}\n\noutput "ready" {\n  value = "ok"\n}\n'
  );
  const init = terraform(fixture, ["init", "-backend=false", "-input=false", "-no-color"]);
  const controlPlan = join(work, "control.plan");
  const planned = terraform(fixture, ["plan", "-input=false", "-lock=false", "-no-color", `-out=${controlPlan}`, "-var", `credential=${syntheticToken}`]);
  if (init.status !== 0 || planned.status !== 0) {
    record(false, "control fixture plans", (init.status !== 0 ? init.text : planned.text).split("\n").slice(-3).join(" "));
    return;
  }
  const control = scanPlan(fixture, controlPlan, syntheticToken);
  record(control.hits.length > 0, "scanner detects a credential passed as a root variable", control.hits.join("; "));

  // The real stack: credential only in the environment.
  const plan = join(work, "cloudflare.plan");
  const planArgs = ["plan", "-input=false", "-lock=false", "-refresh=false", "-no-color", `-out=${plan}`,
    "-var", "enable_custom_domain=true", "-var", "zone_id=synthetic-zone", "-var", "environment=prod"];
  const result = terraform(cloudflareDir, planArgs);
  record(result.status === 0, "cloudflare plan runs with the credential only in the environment", result.status === 0 ? "" : result.text.split("\n").slice(-4).join(" "));
  if (result.status !== 0) return;
  record(!reachedNetwork(result.text), "cloudflare plan made no provider network attempt");
  record(/Plan: 2 to add, 0 to change, 0 to destroy/.test(result.text), "cloudflare plan covers the route and record");

  const scan = scanPlan(cloudflareDir, plan, syntheticToken);
  record(scan.hits.length === 0, `saved cloudflare plan holds no credential (${scan.entries} archive entries, JSON, and text scanned)`, scan.hits.join("; "));
}

// ---------------------------------------------------------------------------------------
// 2. GCP foundation-to-service transition and removal safeguard
// ---------------------------------------------------------------------------------------

function establishedServiceState(work) {
  const schema = JSON.parse(terraform(gcpDir, ["providers", "schema", "-json"]).stdout);
  const service = schema.provider_schemas["registry.terraform.io/hashicorp/google"].resource_schemas.google_cloud_run_v2_service;

  const attributes = {};
  for (const name of Object.keys(service.block.attributes ?? {})) attributes[name] = null;
  for (const [name, block] of Object.entries(service.block.block_types ?? {})) {
    attributes[name] = block.nesting_mode === "list" || block.nesting_mode === "set" ? [] : null;
  }
  Object.assign(attributes, {
    id: "projects/synthetic-project/locations/us-central1/services/rafael-portfolio-backend",
    name: "rafael-portfolio-backend",
    location: "us-central1",
    project: "synthetic-project",
    ingress: "INGRESS_TRAFFIC_ALL",
  });

  const state = {
    version: 4,
    terraform_version: "1.15.8",
    serial: 1,
    lineage: randomUUID(),
    outputs: {},
    resources: [
      {
        mode: "managed",
        type: "google_cloud_run_v2_service",
        name: "backend",
        provider: 'provider["registry.terraform.io/hashicorp/google"]',
        instances: [{ index_key: 0, schema_version: service.version, attributes, sensitive_attributes: [] }],
      },
    ],
    check_results: null,
  };
  const path = join(work, "established-service.tfstate");
  writeFileSync(path, JSON.stringify(state));
  return path;
}

const gcpRunTexts = [];

function gcpPlan(work, label, state, vars, { destroy = false, save = false } = {}) {
  const planPath = join(work, `${label.replace(/\W+/g, "-")}.plan`);
  const args = ["plan", "-input=false", "-lock=false", "-refresh=false", "-no-color", `-state=${state}`];
  if (destroy) args.push("-destroy");
  if (save) args.push(`-out=${planPath}`);
  for (const variable of vars) args.push("-var", variable);
  const result = terraform(gcpDir, args);
  gcpRunTexts.push(result.text);
  const changes = save && result.status === 0
    ? JSON.parse(terraform(gcpDir, ["show", "-json", planPath]).stdout).resource_changes ?? []
    : [];
  return { ...result, changes };
}

function proveServiceTransition(work) {
  const empty = join(work, "empty.tfstate"); // no file: a fresh project
  const established = establishedServiceState(work);
  const persisted = ["create_service=true", `container_image=${IMAGE}`];
  const serviceActions = (changes) =>
    changes.filter((change) => change.address.startsWith("google_cloud_run_v2_service.backend")).flatMap((change) => change.change.actions);

  // Fresh project, default configuration: the safe foundation phase.
  let run = gcpPlan(work, "fresh default", empty, [], { save: true });
  record(run.status === 0, "fresh default plans the foundation");
  record(!run.changes.some((change) => change.address.startsWith("google_cloud_run_v2_service")), "fresh default creates no Cloud Run service or service IAM");
  record(!run.changes.some((change) => change.change.actions.includes("delete")), "fresh default destroys nothing");

  // Moving to the service phase needs the image: fail closed without it.
  run = gcpPlan(work, "flag without image", empty, ["create_service=true"]);
  record(run.status !== 0 && /create_service requires container_image/.test(run.text), "service phase without an image is refused");

  run = gcpPlan(work, "service phase", empty, persisted, { save: true });
  record(run.status === 0 && serviceActions(run.changes).join() === "create", "service phase with the persisted inputs creates the service");
  const serviceIam = run.changes.filter((change) => /^google_cloud_run_v2_service_iam_member\./.test(change.address));
  record(serviceIam.length === 2, "service phase creates the public invoker and the CI deployer binding", `${serviceIam.length} bindings`);

  // Established service, same persisted inputs: ordinary maintenance keeps it.
  run = gcpPlan(work, "maintenance", established, persisted, { save: true });
  const actions = serviceActions(run.changes);
  record(run.status === 0 && !actions.includes("delete") && !actions.includes("create"), "maintenance with the persisted inputs keeps the established service", actions.join() || "no change");
  record(!run.changes.some((change) => change.change.actions.includes("delete")), "maintenance destroys nothing");

  // Established service, inputs lost: must fail closed, never schedule removal.
  run = gcpPlan(work, "inputs lost", established, []);
  record(run.status !== 0 && /Instance cannot be destroyed/.test(run.text) && /prevent_destroy/.test(run.text), "losing the service-phase inputs fails closed (prevent_destroy)");

  run = gcpPlan(work, "flag lost", established, [`container_image=${IMAGE}`]);
  record(run.status !== 0 && /prevent_destroy/.test(run.text), "losing only create_service fails closed");

  run = gcpPlan(work, "image lost", established, ["create_service=true"]);
  record(run.status !== 0 && /create_service requires container_image/.test(run.text), "losing only container_image fails closed");

  run = gcpPlan(work, "destroy", established, persisted, { destroy: true });
  record(run.status !== 0 && /prevent_destroy/.test(run.text), "a destroy plan cannot remove the established service");

  record(!gcpRunTexts.some(reachedNetwork), "service scenarios made no provider network attempt", `${gcpRunTexts.length} plans`);
}

// ---------------------------------------------------------------------------------------

function main() {
  for (const dir of [gcpDir, cloudflareDir]) {
    if (!existsSync(join(dir, ".terraform"))) {
      console.error(`Run \`terraform -chdir=${dir} init -backend=false\` first.`);
      process.exit(2);
    }
  }

  const work = mkdtempSync(join(tmpdir(), "tf-plan-proof-"));
  try {
    proveCredentialSecrecy(work);
    proveServiceTransition(work);
  } finally {
    rmSync(work, { recursive: true, force: true });
  }

  if (failures.length > 0) {
    console.error(`\n${failures.length} Terraform plan proof(s) failed.`);
    process.exit(1);
  }
  console.log("\nAll Terraform plan proofs hold.");
}

main();
