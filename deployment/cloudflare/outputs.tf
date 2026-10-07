output "turnstile_site_key" {
  description = "Public Turnstile site key to embed in frontend"
  value       = cloudflare_turnstile_widget.portfolio.id
}

output "turnstile_secret_key" {
  description = "Turnstile secret key to configure in GCP Secret Manager"
  value       = cloudflare_turnstile_widget.portfolio.secret
  sensitive   = true
}

output "worker_name" {
  description = "Cloudflare Worker service name"
  value       = cloudflare_workers_script.frontend.name
}
