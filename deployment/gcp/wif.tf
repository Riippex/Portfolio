locals {
  # Each stage trusts exactly one branch: dev and staging deploy from develop, prod from main.
  # The GitHub environment name must equal var.environment, so a token minted for another
  # target or branch cannot impersonate this project's deployer.
  trusted_ref = var.environment == "prod" ? "refs/heads/main" : "refs/heads/develop"
}

# Workload Identity Pool for GitHub Actions OIDC federation
resource "google_iam_workload_identity_pool" "github_pool" {
  project                   = var.project_id
  workload_identity_pool_id = var.workload_identity_pool_id
  display_name              = "GitHub Actions Pool"
  description               = "Workload Identity Pool for GitHub Actions CI/CD workflows"

  depends_on = [
    google_project_service.required
  ]
}

# Workload Identity Pool Provider configuring GitHub Actions as OIDC IdP
resource "google_iam_workload_identity_pool_provider" "github_provider" {
  project                            = var.project_id
  workload_identity_pool_id          = google_iam_workload_identity_pool.github_pool.workload_identity_pool_id
  workload_identity_pool_provider_id = var.workload_identity_pool_provider_id
  display_name                       = "GitHub Actions Provider"
  description                        = "OIDC identity provider mapping GitHub Actions claims"

  attribute_mapping = {
    "google.subject"             = "assertion.sub"
    "attribute.actor"            = "assertion.actor"
    "attribute.repository"       = "assertion.repository"
    "attribute.repository_owner" = "assertion.repository_owner"
    "attribute.ref"              = "assertion.ref"
    "attribute.environment"      = "assertion.environment"
  }

  # Repository, GitHub environment, and branch must all match this stage.
  attribute_condition = "assertion.repository == '${var.github_repository}' && assertion.environment == '${var.environment}' && assertion.ref == '${local.trusted_ref}'"

  oidc {
    issuer_uri = "https://token.actions.githubusercontent.com"
  }
}

# Dedicated CI/CD deployer service account (no long-lived JSON keys)
resource "google_service_account" "ci_deployer" {
  project      = var.project_id
  account_id   = var.ci_service_account_id
  display_name = "Portfolio CI Deployer"
  description  = "Least-privilege service account impersonated by GitHub Actions via Workload Identity Federation"

  depends_on = [
    google_project_service.required
  ]
}

# Allow GitHub Actions matching the repository attribute to impersonate the CI deployer SA.
# Impersonation needs the STS and Service Account Credentials APIs, enabled in apis.tf.
resource "google_service_account_iam_member" "workload_identity_user" {
  service_account_id = google_service_account.ci_deployer.name
  role               = "roles/iam.workloadIdentityUser"
  member             = "principalSet://iam.googleapis.com/${google_iam_workload_identity_pool.github_pool.name}/attribute.repository/${var.github_repository}"

  depends_on = [
    google_project_service.required
  ]
}

# Least-privilege role 1: Push images to Artifact Registry
resource "google_artifact_registry_repository_iam_member" "ci_image_pusher" {
  project    = var.project_id
  location   = var.region
  repository = google_artifact_registry_repository.backend.name
  role       = "roles/artifactregistry.writer"
  member     = "serviceAccount:${google_service_account.ci_deployer.email}"
}

# Least-privilege role 2: Deploy new revisions to the Cloud Run service (second bootstrap stage)
resource "google_cloud_run_v2_service_iam_member" "ci_service_deployer" {
  count = var.create_service ? 1 : 0

  project  = var.project_id
  location = var.region
  name     = google_cloud_run_v2_service.backend[0].name
  role     = "roles/run.developer"
  member   = "serviceAccount:${google_service_account.ci_deployer.email}"
}

# Least-privilege role 3: Act as the Cloud Run runtime service account during deployment
resource "google_service_account_iam_member" "ci_act_as_runtime" {
  service_account_id = google_service_account.backend.name
  role               = "roles/iam.serviceAccountUser"
  member             = "serviceAccount:${google_service_account.ci_deployer.email}"
}
