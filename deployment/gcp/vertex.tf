# Optional runtime preparation, not activation. The shared stack still owns the
# APIs/database. No credentials, API calls, store initialization or default tariff.
variable "vertex" {
  description = "Reviewed Vertex settings, or null to preserve the disabled deployment. Permission and model activation are separate explicit gates. No secret values."
  type = object({
    location                = string
    enabled                 = optional(bool, false)
    grant_predict_access    = optional(bool, false)
    data_policy_reviewed    = optional(bool, false)
    token_counting_reviewed = optional(bool, false)
    recovery_ready          = optional(bool, false)
    tariff = object({
      version                      = string
      input_micro_usd_per_million  = number
      output_micro_usd_per_million = number
      valid_through_utc            = string
    })
  })
  default = null

  validation {
    condition = var.vertex == null ? true : (
      var.vertex.location == "global" &&
      can(regex("^[A-Za-z0-9._-]{1,64}$", var.vertex.tariff.version)) &&
      var.vertex.tariff.input_micro_usd_per_million >= 250000 &&
      var.vertex.tariff.output_micro_usd_per_million >= 1500000 &&
      var.vertex.tariff.input_micro_usd_per_million <= 1000000000 &&
      var.vertex.tariff.output_micro_usd_per_million <= 1000000000 &&
      floor(var.vertex.tariff.input_micro_usd_per_million) == var.vertex.tariff.input_micro_usd_per_million &&
      floor(var.vertex.tariff.output_micro_usd_per_million) == var.vertex.tariff.output_micro_usd_per_million &&
      can(regex("^[0-9]{4}-[0-9]{2}-[0-9]{2}$", var.vertex.tariff.valid_through_utc)) &&
      can(formatdate("YYYY-MM-DD", "${var.vertex.tariff.valid_through_utc}T00:00:00Z"))
    )
    error_message = "vertex requires the approved global location, a bounded tariff version, integer rates at or above the reviewed floors and an ISO calendar date. Runtime also checks expiry and the 31-day horizon."
  }

  validation {
    condition = var.vertex == null ? true : (!var.vertex.enabled || (
      var.vertex.grant_predict_access && var.vertex.data_policy_reviewed &&
      var.vertex.token_counting_reviewed && var.vertex.recovery_ready
    ))
    error_message = "Enabling Vertex requires explicit predict access and completed data-policy, token-counting and recovery reviews. These flags are attestations, not a substitute for verification."
  }
}

locals {
  vertex_predict_access = try(var.vertex.grant_predict_access, false)
  vertex_env = var.vertex == null ? {} : {
    "Assistant__Vertex__Enabled"                                      = tostring(var.vertex.enabled)
    "Assistant__Vertex__ProjectId"                                    = var.project_id
    "Assistant__Vertex__Location"                                     = var.vertex.location
    "Assistant__Vertex__DataPolicyReviewed"                           = tostring(var.vertex.data_policy_reviewed)
    "Assistant__Vertex__TokenCountingReviewed"                        = tostring(var.vertex.token_counting_reviewed)
    "Assistant__Vertex__RecoveryReady"                                = tostring(var.vertex.recovery_ready)
    "Assistant__ModelControl__Tariff__Version"                        = var.vertex.tariff.version
    "Assistant__ModelControl__Tariff__ModelId"                        = "gemini-3.1-flash-lite"
    "Assistant__ModelControl__Tariff__InputMicroUsdPerMillionTokens"  = tostring(var.vertex.tariff.input_micro_usd_per_million)
    "Assistant__ModelControl__Tariff__OutputMicroUsdPerMillionTokens" = tostring(var.vertex.tariff.output_micro_usd_per_million)
    "Assistant__ModelControl__Tariff__ValidThroughUtc"                = var.vertex.tariff.valid_through_utc
  }
}

# Project-scoped single-permission role: NOT a per-model IAM restriction. The
# backend pins the model and enforces consent/accounting; a compromised identity
# could use predict elsewhere in this project. No owner/editor/aiplatform.user.
resource "google_project_iam_custom_role" "vertex_predict" {
  count = local.vertex_predict_access ? 1 : 0

  project     = var.project_id
  role_id     = "portfolioVertexPredict_${var.environment}"
  title       = "Portfolio ${var.environment} model prediction"
  description = "Only model prediction for the dedicated portfolio backend runtime."
  permissions = ["aiplatform.endpoints.predict"]
  stage       = "GA"

  lifecycle {
    precondition {
      condition     = var.create_service && contains(["dev", "prod"], var.environment)
      error_message = "Vertex predict access requires the existing service phase and an explicit dev/prod stage."
    }
    precondition {
      condition     = var.control_ledger_project_id == var.project_id && var.control_ledger_database_id != ""
      error_message = "Vertex requires the shared control ledger in the same project."
    }
  }
}

# Additive binding, never an authoritative replacement of project policy. The
# CI/Worker principals receive nothing; disabling the gate revokes this binding.
resource "google_project_iam_member" "vertex_predict" {
  count = local.vertex_predict_access ? 1 : 0

  project = var.project_id
  role    = google_project_iam_custom_role.vertex_predict[0].name
  member  = "serviceAccount:${google_service_account.backend.email}"
}
