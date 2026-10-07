output "worker_name" {
  description = "Name of the frontend Worker this stage routes to (must match the Worker published by the deploy workflow)"
  value       = local.worker_name
}

output "hostname" {
  description = "Hostname routed to the frontend Worker for this stage"
  value       = local.hostname
}
