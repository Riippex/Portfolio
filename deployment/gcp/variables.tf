variable "project_id" {
  type        = string
  description = "Google Cloud Project ID"
  default     = "rafael-portfolio-dev"
}

variable "region" {
  type        = string
  description = "Primary Google Cloud region for serverless deployment"
  default     = "us-central1"
}

variable "environment" {
  type        = string
  description = "Deployment stage name (dev, staging, prod). It names and labels resources and selects the branch the CI identity trusts. It never selects the ASP.NET runtime environment, which is always Production on Cloud Run."
  default     = "dev"

  validation {
    condition     = contains(["dev", "staging", "prod"], var.environment)
    error_message = "environment must be one of: dev, staging, prod."
  }
}

variable "create_service" {
  type        = bool
  description = "Bootstrap gate. Leave false for the first apply (APIs, registry, identities, secret containers). Set true only after the owner has populated the required secret versions and pushed an image, so the service and its service-scoped IAM can be created."
  default     = false
}

variable "service_name" {
  type        = string
  description = "Cloud Run v2 service name for the modular monolith web host"
  default     = "rafael-portfolio-backend"
}

variable "backend_service_account_id" {
  type        = string
  description = "Service account ID for the dedicated Cloud Run runtime identity"
  default     = "sa-portfolio-backend"
}

variable "artifact_repository_id" {
  type        = string
  description = "Artifact Registry Docker repository ID"
  default     = "portfolio"
}

variable "container_image" {
  type        = string
  description = "Immutable reference (tag or digest, never :latest) of an image that already exists in Artifact Registry. It is used only to create the service; later releases are owned by the deploy workflow and Terraform ignores the image afterwards."
  default     = ""
  nullable    = false

  validation {
    condition     = !endswith(var.container_image, ":latest")
    error_message = "container_image must be an immutable tag or digest, not :latest."
  }
}

variable "backend_port" {
  type        = number
  description = "Port exposed by the ASP.NET Core container"
  default     = 8080
}

variable "min_instances" {
  type        = number
  description = "Minimum Cloud Run instances. Defaults to 0 for scale-to-zero cost control."
  default     = 0
}

variable "max_instances" {
  type        = number
  description = "Maximum Cloud Run instances. Bounded for abuse and cost mitigation."
  default     = 2
}

variable "cpu_limit" {
  type        = string
  description = "CPU allocation limit per instance"
  default     = "1000m"
}

variable "memory_limit" {
  type        = string
  description = "Memory allocation limit per instance"
  default     = "512Mi"
}

variable "allow_unauthenticated" {
  type        = bool
  description = "Whether to allow unauthenticated invocations. The backend enforces the signed proxy identity and Turnstile itself, so the edge Worker is the only intended caller."
  default     = true
}

variable "github_repository" {
  type        = string
  description = "GitHub repository in owner/repo format for OIDC trust condition"
  default     = "Riippex/Portfolio"
}

variable "workload_identity_pool_id" {
  type        = string
  description = "Workload Identity Pool ID for GitHub Actions federation"
  default     = "github-actions-pool"
}

variable "workload_identity_pool_provider_id" {
  type        = string
  description = "Workload Identity Pool Provider ID for GitHub Actions OIDC"
  default     = "github-provider"
}

variable "ci_service_account_id" {
  type        = string
  description = "Service account ID for the dedicated CI/CD deployer"
  default     = "sa-portfolio-ci"
}
