# ---------------------------------------------------------------------------
# Ownership
#
# This stack owns the ONE portfolio-wide model control database (Cloud Firestore, Native mode)
# that the Assistant's budget ledger uses: the daily and monthly spending counters, the two
# global permits and the reservation metadata. It exists once for the whole portfolio. Stage
# stacks (deployment/gcp) never declare it; they only receive its project and database id as
# runtime configuration. Two copies of this database would be two independent allowances, so
# creating it is gated, protected against deletion, and never done from a stage.
#
# The stack stores no data: documents are written only by the backend at run time (see
# docs/runbooks/model-control.md). It holds no secret and takes no credential as an input.
# ---------------------------------------------------------------------------

locals {
  # Collections whose documents may carry an expiresAt timestamp: the period counters and the
  # reservation metadata. Counters get expiresAt 40 days after their period ends; a reservation
  # gets it only once it is resolved, so an unresolved (Active or Uncertain) reservation has no
  # expiresAt field and TTL cannot match it. Physical TTL deletion is asynchronous (often within
  # hours, not guaranteed); the backend treats expiry logically and never relies on it.
  ttl_collections = toset(["model_control_periods", "model_control_reservations"])

  database_resource = "projects/${var.project_id}/databases/${var.database_id}"
}

resource "google_project_service" "firestore" {
  count = var.create_database ? 1 : 0

  project            = var.project_id
  service            = "firestore.googleapis.com"
  disable_on_destroy = false
}

resource "google_firestore_database" "control" {
  count = var.create_database ? 1 : 0

  project     = var.project_id
  name        = var.database_id
  location_id = var.location_id
  type        = "FIRESTORE_NATIVE"

  # The allowance lives here: losing the database would reset the counters, so deletion is
  # blocked twice (the service flag and Terraform) and an explicit decommissioning change is
  # required to remove either.
  delete_protection_state           = "DELETE_PROTECTION_ENABLED"
  deletion_policy                   = "ABANDON"
  point_in_time_recovery_enablement = "POINT_IN_TIME_RECOVERY_DISABLED"

  lifecycle {
    prevent_destroy = true
  }

  depends_on = [google_project_service.firestore]
}

resource "google_firestore_field" "ttl" {
  for_each = var.create_database ? local.ttl_collections : toset([])

  project    = var.project_id
  database   = google_firestore_database.control[0].name
  collection = each.value
  field      = "expiresAt"

  # TTL only. The field keeps its default index because the backend range-queries expiresAt
  # when it reclaims expired metadata.
  ttl_config {}
}

# Least privilege: each stage's backend runtime identity may read and write documents in this
# one database and nothing else. The role is granted at project level because Firestore has no
# database-level IAM binding resource; the condition limits it to this database, and a
# condition that does not match denies access (the backend then keeps paid work disabled).
resource "google_project_iam_member" "runtime" {
  for_each = var.create_database ? toset(var.runtime_service_accounts) : toset([])

  project = var.project_id
  role    = "roles/datastore.user"
  member  = "serviceAccount:${each.value}"

  condition {
    title       = "portfolio-model-control-database-only"
    description = "Limits Firestore access to the shared model control database."
    expression  = "resource.name == \"${local.database_resource}\""
  }

  depends_on = [google_firestore_database.control]
}
