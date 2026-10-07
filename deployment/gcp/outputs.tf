output "backend_uri" {
  description = "Assigned URI for the Cloud Run backend service"
  value       = google_cloud_run_v2_service.backend.uri
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
  description = "Map of created secret IDs in Google Secret Manager"
  value = {
    turnstile_secret_key  = google_secret_manager_secret.turnstile_secret_key.secret_id
    proxy_identity_secret = google_secret_manager_secret.proxy_identity_secret.secret_id
    contact_api_token     = google_secret_manager_secret.contact_api_token.secret_id
  }
}
