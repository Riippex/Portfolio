import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { contactDeploymentViolations } from "./contact-deployment-policy.mjs";

const mainPath = "deployment/gcp/main.tf";
const contactPath = "deployment/gcp/contact.tf";
const files = Object.fromEntries([mainPath, contactPath].map((path) => [path, readFileSync(new URL(`../${path}`, import.meta.url), "utf8")]));
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
]) {
  test(`rejects ${name}`, () => {
    assert.ok(files[path].includes(before), "mutation must change the real source");
    assert.ok(contactDeploymentViolations({ ...files, [path]: files[path].replace(before, after) }).length > 0);
  });
}
