import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { contactDeploymentViolations } from "./contact-deployment-policy.mjs";

const mainPath = "deployment/gcp/main.tf";
const contactPath = "deployment/gcp/contact.tf";
const workerPath = "services/contact-email/wrangler.jsonc";
const workflowPath = ".github/workflows/deploy-contact-relay.yml";
const hostPath = "backend/src/Hosts/Web/Rafael.Portfolio.Web/Program.cs";
const files = Object.fromEntries([mainPath, contactPath, workerPath, workflowPath, hostPath].map((path) => [path, readFileSync(new URL(`../${path}`, import.meta.url), "utf8")]));
test("repository Contact preparation preserves every offline tripwire", () => {
  assert.deepEqual(contactDeploymentViolations(files), []);
});
for (const [name, path, before, after] of [
  ["default object activates preparation", contactPath, "default = null", "default = {}"],
  ["activation defaults true", contactPath, "enabled                  = optional(bool, false)", "enabled                  = optional(bool, true)"],
  ["cleanup review bypass", contactPath, "var.contact.mailbox_cleanup_reviewed &&", "true &&"],
  ["delivery review bypass", contactPath, "var.contact.delivery_verified\n", "true\n"],
  ["always prepared secrets", contactPath, "try(var.contact.prepare_secrets, false) ?", "true ?"],
  ["disabled service still reads secrets", contactPath, "contact_runtime_secrets = local.contact_enabled ?", "contact_runtime_secrets = true ?"],
  ["always enabled service", mainPath, "value = tostring(local.contact_enabled)", 'value = "true"'],
  ["ungated credential injection", mainPath, "locals {", 'locals {\n  unsafe = "Contact__ApiToken"'],
  ["Worker endpoint available while disabled", contactPath, "contact_runtime_env = local.contact_enabled &&", "contact_runtime_env = true &&"],
  ["unused account secret still required", contactPath, 'name != "Contact__AccountId"', "true"],
  ["unknown relay mode accepted", contactPath, 'contains(["rest", "worker"], var.contact.relay_mode)', "true"],
  ["Worker logs enabled", workerPath, '"enabled": false', '"enabled": true'],
  ["Worker enabled by default", workerPath, '"CONTACT_RELAY_ENABLED": "false"', '"CONTACT_RELAY_ENABLED": "true"'],
  ["Worker finite limit removed", workerPath, '"limit": 6', '"limit": 600000'],
  ["Worker secret baked in", workerPath, '"PORTFOLIO_STAGE": "dev"', '"RELAY_TOKEN": "synthetic", "PORTFOLIO_STAGE": "dev"'],
  ["release without owner gate", workflowPath, "environment: ${{ inputs.environment }}", "environment: ignored"],
  ["release reuses frontend credential", workflowPath, "secrets.CLOUDFLARE_CONTACT_API_TOKEN", "secrets.CLOUDFLARE_API_TOKEN"],
  ["release without validation", workflowPath, "needs: [authorize, validate]", "needs: authorize"],
  ["release from wrong branch", workflowPath, "dev:refs/heads/develop|prod:refs/heads/main", "dev:refs/heads/main|prod:refs/heads/develop"],
  ["host follows redirects", hostPath, "AllowAutoRedirect = false", "AllowAutoRedirect = true"],
  ["host skips stage validation", hostPath, "contactOptions.ValidateStage(", "SkipStageValidation("],
]) {
  test(`rejects ${name}`, () => {
    assert.ok(files[path].includes(before), "mutation must change the real source");
    assert.ok(contactDeploymentViolations({ ...files, [path]: files[path].replace(before, after) }).length > 0);
  });
}
