# Cloudflare Turnstile CAPTCHA widget for bot and abuse mitigation
resource "cloudflare_turnstile_widget" "portfolio" {
  account_id = var.account_id
  name       = "rafael-portfolio-${var.environment}"
  domains    = [var.domain_name, "localhost"]
  mode       = "managed"
  region     = "world"
}

# Cloudflare Workers script definition for the Next.js frontend
resource "cloudflare_workers_script" "frontend" {
  account_id          = var.account_id
  name                = var.worker_name
  content             = file("${path.module}/scripts/worker_placeholder.js")
  module              = true
  compatibility_date  = "2026-09-24"
  compatibility_flags = ["nodejs_compat"]

  plain_text_binding {
    name = "PORTFOLIO_BACKEND_URL"
    text = var.backend_url
  }

  plain_text_binding {
    name = "NEXT_PUBLIC_TURNSTILE_SITE_KEY"
    text = cloudflare_turnstile_widget.portfolio.id
  }

  secret_text_binding {
    name = "ASSISTANT_PROXY_IDENTITY_SECRET"
    text = var.proxy_identity_secret
  }
}

# Worker route binding to the apex custom domain (optional until domain is verified)
resource "cloudflare_workers_route" "custom_domain" {
  count       = var.enable_custom_domain && var.zone_id != "" ? 1 : 0
  zone_id     = var.zone_id
  pattern     = "${var.domain_name}/*"
  script_name = cloudflare_workers_script.frontend.name
}

# DNS record for apex domain pointing to Cloudflare edge proxy
resource "cloudflare_record" "apex" {
  count   = var.enable_custom_domain && var.zone_id != "" ? 1 : 0
  zone_id = var.zone_id
  name    = "@"
  type    = "A"
  content = "192.0.2.1"
  proxied = true
  ttl     = 1
}
