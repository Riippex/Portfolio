# ---------------------------------------------------------------------------
# Ownership
#
# Terraform owns the durable shape of the backend: APIs, registry, identities, secret
# containers, scaling, resource limits, the runtime configuration below, and IAM.
# The deploy workflow owns the running release: the Cloud Run image and the deploy
# metadata Cloud Run records when it updates a revision. Terraform ignores exactly those
# attributes on the service (see lifecycle) and nothing else, so infrastructure
# maintenance cannot reset a released image and a release cannot change security,
# scaling, environment, or IAM settings.
#
# Secret payloads are never part of Terraform. Only the empty secret containers are
# declared here; the owner adds the values out of band (see docs/runbooks/infrastructure.md).
# ---------------------------------------------------------------------------

locals {
  # Remotely deployed backends always run with production-grade enforcement: the signed
  # proxy identity is required and Turnstile fails closed. The deployment stage
  # (var.environment) never selects this value; Development stays a local-only mode.
  aspnetcore_environment = "Production"

  # Secrets the service reads at startup. The contact API token is intentionally absent:
  # Contact is disabled, so it needs no credentials until a separate, owner-authorized
  # activation adds them.
  runtime_secrets = {
    turnstile_secret_key  = "turnstile-secret-key"
    proxy_identity_secret = "proxy-identity-secret"
  }
}

# Artifact Registry Docker repository for container images
resource "google_artifact_registry_repository" "backend" {
  project       = var.project_id
  location      = var.region
  repository_id = var.artifact_repository_id
  description   = "Docker container repository for Rafael.Portfolio backend"
  format        = "DOCKER"

  labels = {
    stage = var.environment
  }

  depends_on = [
    google_project_service.required
  ]
}

# Dedicated least-privilege runtime service account for Cloud Run
resource "google_service_account" "backend" {
  project      = var.project_id
  account_id   = var.backend_service_account_id
  display_name = "Portfolio Backend Cloud Run Runtime Account"
  description  = "Least-privilege runtime identity for Rafael.Portfolio.Web"

  depends_on = [
    google_project_service.required
  ]
}

# Empty Secret Manager containers. The owner populates versions out of band.
resource "google_secret_manager_secret" "runtime" {
  for_each = local.runtime_secrets

  project   = var.project_id
  secret_id = each.value

  labels = {
    stage = var.environment
  }

  replication {
    auto {}
  }

  depends_on = [
    google_project_service.required
  ]
}

# Granular IAM: the runtime account can read only these specific secrets
resource "google_secret_manager_secret_iam_member" "runtime_accessor" {
  for_each = local.runtime_secrets

  project   = var.project_id
  secret_id = google_secret_manager_secret.runtime[each.key].secret_id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${google_service_account.backend.email}"
}

# Cloud Run v2 service for the Rafael.Portfolio.Web modular monolith.
# Created only in the second bootstrap stage (create_service = true).
resource "google_cloud_run_v2_service" "backend" {
  count = var.create_service ? 1 : 0

  name     = var.service_name
  location = var.region
  ingress  = "INGRESS_TRAFFIC_ALL"

  # Cloud Run refuses to delete the service while this is set, as a second line of defence
  # behind the plan-time prevent_destroy below.
  deletion_protection = true

  # Manage service-level defaults returned by the API as well as revision limits.
  # Keeping this explicit avoids a perpetual plan to remove the scaling block.
  scaling {
    scaling_mode       = "AUTOMATIC"
    min_instance_count = var.min_instances
  }

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
        # With explicit limits, Cloud Run requires this to retain request-based billing.
        cpu_idle = true
        limits = {
          cpu    = var.cpu_limit
          memory = var.memory_limit
        }
      }

      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = local.aspnetcore_environment
      }

      env {
        name  = "ASPNETCORE_URLS"
        value = "http://+:${var.backend_port}"
      }

      # The backend stage is explicit and never inferred from the ASP.NET environment, which
      # is always Production here. It must equal the stage inside every signed identity, and
      # dev additionally closes every route except /health to callers without one.
      env {
        name  = "Portfolio__Stage"
        value = var.environment
      }

      env {
        name  = "Contact__Enabled"
        value = "false"
      }

      env {
        name = "Turnstile__SecretKey"
        value_source {
          secret_key_ref {
            secret  = google_secret_manager_secret.runtime["turnstile_secret_key"].secret_id
            version = "latest"
          }
        }
      }

      env {
        name = "AssistantSecurity__ProxyIdentitySecret"
        value_source {
          secret_key_ref {
            secret  = google_secret_manager_secret.runtime["proxy_identity_secret"].secret_id
            version = "latest"
          }
        }
      }
    }
  }

  lifecycle {
    # Fail closed against losing an established service. count depends on create_service, so a
    # plan made without the saved service-phase inputs (create_service = true plus
    # container_image, kept in the stage's ignored tfvars file and passed to every plan and
    # apply) would otherwise silently schedule the service and its IAM for removal. With this
    # set, that plan errors instead. Decommissioning is a deliberate edit: remove this line
    # and deletion_protection in a reviewed change first (see docs/runbooks/infrastructure.md).
    prevent_destroy = true

    # The deploy workflow owns exactly these. Everything else on the service stays managed.
    ignore_changes = [
      client,
      client_version,
      labels,
      template[0].labels,
      template[0].containers[0].image,
    ]

    precondition {
      condition     = var.container_image != ""
      error_message = "create_service requires container_image: push an image to Artifact Registry first and pass its immutable reference."
    }

    precondition {
      condition     = contains(["dev", "prod"], var.environment)
      error_message = "The backend serves only the dev and prod stages (Portfolio__Stage); it refuses to start for any other stage name."
    }
  }

  depends_on = [
    google_secret_manager_secret_iam_member.runtime_accessor
  ]
}

# Public invocation. Callers still need the signed proxy identity and Turnstile.
resource "google_cloud_run_v2_service_iam_member" "public_invoker" {
  count = var.create_service && var.allow_unauthenticated ? 1 : 0

  project  = var.project_id
  location = var.region
  name     = google_cloud_run_v2_service.backend[0].name
  role     = "roles/run.invoker"
  member   = "allUsers"
}
