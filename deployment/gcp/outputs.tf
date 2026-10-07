output "backend_uri" {
  description = "Assigned URI for the Cloud Run backend service (null until create_service is true)"
  value       = one(google_cloud_run_v2_service.backend[*].uri)
}

output "artifact_registry_repository_id" {
  description = "Artifact Registry repository ID for container images"
  value       = google_artifact_registry_repository.backend.repository_id
}

output "service_account_email" {
  description = "Dedicated runtime service account email"
  value       = google_service_account.backend.email
}

output "secret_manager_secret_ids" {
  description = "Secret container IDs the owner populates out of band (names only, never values)"
  value       = { for key, secret in google_secret_manager_secret.runtime : key => secret.secret_id }
}

output "workload_identity_provider" {
  description = "Workload Identity Provider resource name for GitHub Actions auth (projects/{project_number}/locations/global/workloadIdentityPools/{pool}/providers/{provider})"
  value       = google_iam_workload_identity_pool_provider.github_provider.name
}

output "ci_service_account_email" {
  description = "Dedicated CI deployer service account email to impersonate in GitHub Actions"
  value       = google_service_account.ci_deployer.email
}
