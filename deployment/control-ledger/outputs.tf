output "firestore_project_id" {
  description = "Value for each stage's control_ledger_project_id (names only, never data)"
  value       = var.project_id
}

output "firestore_database_id" {
  description = "Value for each stage's control_ledger_database_id"
  value       = var.database_id
}

output "database_created" {
  description = "Whether this stack currently declares the shared control database"
  value       = var.create_database
}
