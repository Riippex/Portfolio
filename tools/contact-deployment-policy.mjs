// Offline tripwires; actual default/preparation/activation behavior is also
// exercised by the synthetic Terraform plan proofs. No provider access here.
export function contactDeploymentViolations(files) {
  const strip = (s = "") => s.replace(/\/\*[\s\S]*?\*\//g, "").split("\n")
    .filter((line) => !/^\s*(#|\/\/)/.test(line)).join("\n");
  const main = strip(files["deployment/gcp/main.tf"]);
  const contact = strip(files["deployment/gcp/contact.tf"]);
  const failures = [];
  if (!/variable\s+"contact"[\s\S]*?default\s*=\s*null/.test(contact)) failures.push("Contact must default to null");
  for (const flag of ["prepare_secrets", "enabled", "provider_reviewed", "mailbox_cleanup_reviewed", "delivery_verified"]) {
    if (!new RegExp(`${flag}\\s*=\\s*optional\\(bool,\\s*false\\)`).test(contact)) failures.push(`Contact ${flag} must default false`);
  }
  if (!/!var\.contact\.enabled\s*\|\|\s*\(\s*var\.contact\.prepare_secrets\s*&&\s*var\.contact\.provider_reviewed\s*&&\s*var\.contact\.mailbox_cleanup_reviewed\s*&&\s*var\.contact\.delivery_verified/.test(contact)) {
    failures.push("Contact enablement must require preparation and every review");
  }
  if (!/contact_enabled\s*=\s*try\(var\.contact\.enabled,\s*false\)/.test(contact) ||
      !/name\s*=\s*"Contact__Enabled"\s*\n\s*value\s*=\s*tostring\(local\.contact_enabled\)/.test(main)) {
    failures.push("Contact__Enabled must use the disabled-by-default gate");
  }
  if (!/contact_prepared_secrets\s*=\s*try\(var\.contact\.prepare_secrets,\s*false\)\s*\?\s*local\.contact_secret_ids\s*:\s*\{\}/.test(contact) ||
      !/runtime_secrets\s*=\s*merge\(\{[\s\S]*?\},\s*local\.contact_prepared_secrets\)/.test(main)) {
    failures.push("Contact containers must be preparation-gated");
  }
  if (!/contact_runtime_secrets\s*=\s*local\.contact_enabled\s*\?\s*\{[\s\S]*?\}\s*:\s*\{\}/.test(contact) ||
      !/dynamic\s+"env"\s*\{\s*for_each\s*=\s*local\.contact_runtime_secrets/.test(main)) {
    failures.push("disabled Contact must not read secret versions");
  }
  if (!/secret\s*=\s*google_secret_manager_secret\.runtime\[env\.value\]\.secret_id/.test(main) ||
      /Contact__(?:AccountId|ApiToken|SenderEmail|RecipientEmail)|contact[-_]api[-_]token/.test(main)) {
    failures.push("the service must not depend on ungated Contact credentials");
  }
  if (!/!var\.contact\.prepare_secrets\s*\|\|\s*\(var\.create_service\s*&&\s*contains\(\["dev",\s*"prod"\],\s*var\.environment\)\)/.test(main)) {
    failures.push("Contact preparation requires the established dev/prod service phase");
  }
  return failures;
}
