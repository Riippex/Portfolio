# Google Cloud service APIs the stack needs on a fresh project. Everything that creates a
# resource through one of these APIs depends on the matching entry, so a first apply enables
# the API before using it.
#
# Prerequisite outside Terraform: the Service Usage API (serviceusage.googleapis.com) must
# already be enabled, because Terraform uses it to enable the others. It is on by default
# for new projects.
locals {
  required_services = toset([
    # Workload.
    "run.googleapis.com",
    "artifactregistry.googleapis.com",
    "secretmanager.googleapis.com",
    # Identity and keyless CI federation. Impersonating the deployer service account from
    # GitHub Actions exchanges the OIDC token through the Security Token Service and then
    # mints an access token through the Service Account Credentials API.
    "iam.googleapis.com",
    "iamcredentials.googleapis.com",
    "sts.googleapis.com",
    # Used by the documented bootstrap and by project-level lookups.
    "cloudresourcemanager.googleapis.com",
  ])
}

resource "google_project_service" "required" {
  for_each = local.required_services

  project            = var.project_id
  service            = each.value
  disable_on_destroy = false
}
