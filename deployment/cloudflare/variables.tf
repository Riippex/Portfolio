variable "cloudflare_api_token" {
  type        = string
  description = "Cloudflare API token with Workers, DNS, and Turnstile permissions"
  sensitive   = true
  default     = "placeholder-api-token"
}

variable "account_id" {
  type        = string
  description = "Cloudflare Account ID"
  default     = "placeholder-account-id"
}

variable "zone_id" {
  type        = string
  description = "Cloudflare Zone ID (optional if DNS is managed separately)"
  default     = ""
}

variable "domain_name" {
  type        = string
  description = "Apex domain name for the portfolio"
  default     = "rafaelpatinodiaz.com"
}

variable "environment" {
  type        = string
  description = "Deployment environment name (dev, staging, prod)"
  default     = "dev"
}

variable "worker_name" {
  type        = string
  description = "Cloudflare Worker name for the Next.js frontend"
  default     = "rafael-portfolio-frontend"
}

variable "backend_url" {
  type        = string
  description = "Public URL of the backend Cloud Run service to bind into the frontend proxy"
  default     = "https://portfolio-backend-dev.a.run.app"
}

variable "proxy_identity_secret" {
  type        = string
  description = "Shared HMAC secret for trusted edge identity propagation (X-Client-Key-Proof)"
  sensitive   = true
  default     = "placeholder-proxy-identity-secret"
}

variable "enable_custom_domain" {
  type        = bool
  description = "Whether to configure custom domain DNS records and worker routes"
  default     = false
}
