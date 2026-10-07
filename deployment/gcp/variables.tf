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
  description = "Deployment environment name (dev, staging, prod)"
  default     = "dev"
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
  description = "Container image URI for Rafael.Portfolio.Web (built via root Dockerfile)"
  default     = "us-central1-docker.pkg.dev/rafael-portfolio-dev/portfolio/backend:latest"
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
  description = "Whether to allow unauthenticated invocations (ingress routed via Cloudflare proxy)"
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
