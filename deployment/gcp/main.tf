# Google Cloud Service APIs required for the backend workload
resource "google_project_service" "run" {
  project            = var.project_id
  service            = "run.googleapis.com"
  disable_on_destroy = false
}

resource "google_project_service" "artifactregistry" {
  project            = var.project_id
  service            = "artifactregistry.googleapis.com"
  disable_on_destroy = false
}

resource "google_project_service" "secretmanager" {
  project            = var.project_id
  service            = "secretmanager.googleapis.com"
  disable_on_destroy = false
}

# Artifact Registry Docker repository for container images
resource "google_artifact_registry_repository" "backend" {
  project       = var.project_id
  location      = var.region
  repository_id = var.artifact_repository_id
  description   = "Docker container repository for Rafael.Portfolio backend"
  format        = "DOCKER"

  depends_on = [
    google_project_service.artifactregistry
  ]
}

# Dedicated least-privilege runtime service account for Cloud Run
resource "google_service_account" "backend" {
  project      = var.project_id
  account_id   = var.backend_service_account_id
  display_name = "Portfolio Backend Cloud Run Runtime Account"
  description  = "Least-privilege runtime identity for Rafael.Portfolio.Web"
}

# Secret Manager definitions for sensitive backend credentials
resource "google_secret_manager_secret" "turnstile_secret_key" {
  project   = var.project_id
  secret_id = "turnstile-secret-key"

  replication {
    auto {}
  }

  depends_on = [
    google_project_service.secretmanager
  ]
}

resource "google_secret_manager_secret" "proxy_identity_secret" {
  project   = var.project_id
  secret_id = "proxy-identity-secret"

  replication {
    auto {}
  }

  depends_on = [
    google_project_service.secretmanager
  ]
}

resource "google_secret_manager_secret" "contact_api_token" {
  project   = var.project_id
  secret_id = "contact-api-token"

  replication {
    auto {}
  }

  depends_on = [
    google_project_service.secretmanager
  ]
}

# Granular IAM bindings: Cloud Run runtime account only accesses these specific secrets
resource "google_secret_manager_secret_iam_member" "turnstile_secret_accessor" {
  project   = var.project_id
  secret_id = google_secret_manager_secret.turnstile_secret_key.secret_id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${google_service_account.backend.email}"
}

resource "google_secret_manager_secret_iam_member" "proxy_secret_accessor" {
  project   = var.project_id
  secret_id = google_secret_manager_secret.proxy_identity_secret.secret_id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${google_service_account.backend.email}"
}

resource "google_secret_manager_secret_iam_member" "contact_token_accessor" {
  project   = var.project_id
  secret_id = google_secret_manager_secret.contact_api_token.secret_id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${google_service_account.backend.email}"
}

# Cloud Run v2 service for Rafael.Portfolio.Web modular monolith
resource "google_cloud_run_v2_service" "backend" {
  name     = var.service_name
  location = var.region
  ingress  = "INGRESS_TRAFFIC_ALL"

  template {
    service_account = google_service_account.backend.email

    scaling {
      min_instance_count = var.min_instances
      max_instance_count = var.max_instances
    }

    containers {
      image = var.container_image

      ports {
        container_port = var.backend_port
      }

      resources {
        limits = {
          cpu    = var.cpu_limit
          memory = var.memory_limit
        }
      }

      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = var.environment == "prod" ? "Production" : "Development"
      }

      env {
        name  = "ASPNETCORE_URLS"
        value = "http://+:${var.backend_port}"
      }

      env {
        name  = "Contact__Enabled"
        value = "false"
      }

      env {
        name = "Turnstile__SecretKey"
        value_source {
          secret_key_ref {
            secret  = google_secret_manager_secret.turnstile_secret_key.secret_id
            version = "latest"
          }
        }
      }

      env {
        name = "AssistantSecurity__ProxyIdentitySecret"
        value_source {
          secret_key_ref {
            secret  = google_secret_manager_secret.proxy_identity_secret.secret_id
            version = "latest"
          }
        }
      }

      env {
        name = "Contact__ApiToken"
        value_source {
          secret_key_ref {
            secret  = google_secret_manager_secret.contact_api_token.secret_id
            version = "latest"
          }
        }
      }
    }
  }

  depends_on = [
    google_project_service.run,
    google_secret_manager_secret_iam_member.turnstile_secret_accessor,
    google_secret_manager_secret_iam_member.proxy_secret_accessor,
    google_secret_manager_secret_iam_member.contact_token_accessor
  ]
}

# Public invocation IAM member (when routed from Cloudflare edge proxy)
resource "google_cloud_run_v2_service_iam_member" "public_invoker" {
  count    = var.allow_unauthenticated ? 1 : 0
  project  = var.project_id
  location = var.region
  name     = google_cloud_run_v2_service.backend.name
  role     = "roles/run.invoker"
  member   = "allUsers"
}
