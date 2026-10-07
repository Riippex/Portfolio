# ---------------------------------------------------------------------------
# Ownership
#
# Terraform owns the zone routing for the frontend: the stage hostname's DNS record and the
# Worker route that points it at the deployed Worker.
#
# Terraform does NOT own, and must not be extended to own:
#   - the Worker script, its runtime variables, or its secrets. `wrangler deploy` publishes
#     the application and writes the public runtime variable PORTFOLIO_BACKEND_URL. The
#     proxy identity secret (ASSISTANT_PROXY_IDENTITY_SECRET) is set out of band on the
#     exact Worker with `wrangler secret put`. Keeping them out of Terraform means an
#     infrastructure apply can neither replace released code nor persist a secret in state.
#   - the Turnstile widget. It is created in the Cloudflare console because the provider
#     stores the widget's computed secret in state. Only the public site key is used, as the
#     NEXT_PUBLIC_TURNSTILE_SITE_KEY build variable.
# ---------------------------------------------------------------------------

locals {
  # Same derivation as the env name in frontend/wrangler.jsonc.
  worker_name = "${var.worker_name_prefix}-${var.environment}"
  hostname    = var.environment == "prod" ? var.domain_name : "${var.environment}.${var.domain_name}"
  record_name = var.environment == "prod" ? "@" : var.environment
  routing     = var.enable_custom_domain && var.zone_id != ""
}

check "custom_domain_inputs" {
  assert {
    condition     = !var.enable_custom_domain || var.zone_id != ""
    error_message = "enable_custom_domain requires zone_id; no DNS record or route will be created without it."
  }
}

# Worker route for the stage hostname. The Worker must already be deployed under local.worker_name.
resource "cloudflare_workers_route" "custom_domain" {
  count       = local.routing ? 1 : 0
  zone_id     = var.zone_id
  pattern     = "${local.hostname}/*"
  script_name = local.worker_name
}

# Proxied DNS record so the stage hostname reaches the Cloudflare edge
resource "cloudflare_record" "stage_hostname" {
  count   = local.routing ? 1 : 0
  zone_id = var.zone_id
  name    = local.record_name
  type    = "A"
  content = "192.0.2.1"
  proxied = true
  ttl     = 1
}
