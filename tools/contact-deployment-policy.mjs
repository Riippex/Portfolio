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
  if (!/relay_mode\s*=\s*optional\(string,\s*"rest"\)/.test(contact) ||
      !/contains\(\["rest",\s*"worker"\],\s*var\.contact\.relay_mode\)/.test(contact) ||
      !/contact_runtime_env\s*=\s*local\.contact_enabled\s*&&/.test(contact) ||
      !/name\s*!=\s*"Contact__AccountId"/.test(contact) ||
      !/stage-specific HTTPS contact Worker endpoint/.test(contact) ||
      !/for_each\s*=\s*local\.contact_runtime_env/.test(main)) {
    failures.push("Worker mode must be opt-in, stage-bound, disabled-gated and omit the unused account credential");
  }
  try {
    const worker = JSON.parse(files["services/contact-email/wrangler.jsonc"] ?? "{}");
    const namespaces = new Set();
    for (const stage of ["dev", "prod"]) {
      const env = worker.env?.[stage];
      if (worker.observability?.enabled !== false || env?.name !== `rafael-portfolio-contact-${stage}` ||
          env?.workers_dev !== true || env.vars?.PORTFOLIO_STAGE !== stage || env.vars?.CONTACT_RELAY_ENABLED !== "false" ||
          Object.keys(env.vars ?? {}).sort().join(",") !== "CONTACT_RELAY_ENABLED,PORTFOLIO_STAGE" ||
          JSON.stringify(env.send_email) !== '[{"name":"EMAIL"}]' || env.ratelimits?.length !== 1 ||
          env.ratelimits?.[0]?.name !== "RELAY_RATE_LIMIT" || env.ratelimits?.[0]?.simple?.limit !== 6 ||
          env.ratelimits?.[0]?.simple?.period !== 60) failures.push(`Contact Worker ${stage} must default off with no secret values, finite limits and disabled payload logs`);
      namespaces.add(env?.ratelimits?.[0]?.namespace_id);
    }
    if (namespaces.size !== 2 || [...namespaces].some((id) => !["1301", "1401"].includes(id))) failures.push("Contact Worker must use separate reserved rate namespaces");
  } catch { failures.push("Contact Worker configuration must parse"); }
  const workflow = files[".github/workflows/deploy-contact-relay.yml"] ?? "";
  if (!/workflow_dispatch:/.test(workflow) || /^  (push|pull_request|schedule):/m.test(workflow) ||
      !/enable_relay:[\s\S]*?default: false/.test(workflow) ||
      !workflow.includes("dev:refs/heads/develop|prod:refs/heads/main") ||
      !workflow.includes("needs: [authorize, validate]") ||
      !workflow.includes("uses: ./.github/workflows/ci.yml") ||
      !workflow.includes("environment: ${{ inputs.environment }}") ||
      !workflow.includes("ref: ${{ github.sha }}") || !workflow.includes("if: inputs.enable_relay") ||
      /secrets\.(?!CLOUDFLARE_API_TOKEN\b)|id-token:\s*write/.test(workflow)) {
    failures.push("Contact Worker release must be manual, branch/stage paired, exact-source validated, owner gated and disabled by default");
  }
  const host = files["backend/src/Hosts/Web/Rafael.Portfolio.Web/Program.cs"] ?? "";
  if (!host.includes("contactOptions.ValidateStage(") || !/AllowAutoRedirect\s*=\s*false/.test(host) ||
      !/UseCookies\s*=\s*false/.test(host)) failures.push("Contact transport must validate stage and prevent credential redirects/cookies");
  return failures;
}
