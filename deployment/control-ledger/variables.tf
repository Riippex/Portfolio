variable "project_id" {
  type        = string
  description = "The one Google Cloud project that hosts the portfolio-wide model control database. Every stage's backend runtime identity reaches this single database, so the dev and prod deployments share one allowance. There is deliberately no default."
}

variable "location_id" {
  type        = string
  description = "Firestore location of the control database. It cannot be changed after creation."
  default     = "us-central1"
}

variable "database_id" {
  type        = string
  description = "Firestore database ID. Stage stacks receive this value as Assistant__ModelControl__Firestore__DatabaseId."
  default     = "portfolio-control"

  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{2,61}[a-z0-9]$", var.database_id))
    error_message = "database_id must be 4 to 63 lowercase letters, digits or hyphens, starting with a letter."
  }
}

variable "create_database" {
  type        = bool
  description = "Activation gate. Leave false until the owner explicitly authorizes creating the shared control database; while false the stack declares and creates nothing. Apply this stack once per portfolio, never from a stage stack."
  default     = false
}

variable "runtime_service_accounts" {
  type        = list(string)
  description = "Backend runtime service account emails (one per stage, from each stage's service_account_email output) that may use the control database. Each receives roles/datastore.user restricted by an IAM condition to this database only."
  default     = []

  validation {
    condition     = alltrue([for email in var.runtime_service_accounts : length(split("@", email)) == 2 && endswith(email, ".iam.gserviceaccount.com")])
    error_message = "runtime_service_accounts must be service account emails, which end in .iam.gserviceaccount.com."
  }
}
