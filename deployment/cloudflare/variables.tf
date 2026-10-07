variable "cloudflare_api_token" {
  type        = string
  description = "Cloudflare API token with Zone, DNS, and Workers Routes permissions. Provider credentials are not stored in Terraform state."
  sensitive   = true
  default     = "placeholder-api-token"
}

variable "zone_id" {
  type        = string
  description = "Cloudflare Zone ID. Required when enable_custom_domain is true."
  default     = ""
}

variable "domain_name" {
  type        = string
  description = "Apex domain name for the portfolio"
  default     = "rafaelpatinodiaz.com"
}

variable "environment" {
  type        = string
  description = "Deployment stage (dev or prod). Selects the Worker name and hostname; it must match the stage the deploy workflow builds and deploys."
  default     = "dev"

  validation {
    condition     = contains(["dev", "prod"], var.environment)
    error_message = "environment must be one of: dev, prod."
  }
}

variable "worker_name_prefix" {
  type        = string
  description = "Base name of the frontend Worker. The deployed Worker is \"<prefix>-<environment>\", which must equal the env name in frontend/wrangler.jsonc."
  default     = "rafael-portfolio-frontend"
}

variable "enable_custom_domain" {
  type        = bool
  description = "Whether to create the stage hostname DNS record and Worker route. Enable only after the Worker has been deployed once."
  default     = false
}
