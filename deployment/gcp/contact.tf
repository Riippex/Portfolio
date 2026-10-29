# Contact preparation creates empty containers only. Payloads are populated out
# of band; no address, account identifier or token is a Terraform input/value.
variable "contact" {
  description = "Optional Contact preparation and reviewed activation. Null leaves Contact off and creates no Contact secrets."
  type = object({
    prepare_secrets          = optional(bool, false)
    enabled                  = optional(bool, false)
    provider_reviewed        = optional(bool, false)
    mailbox_cleanup_reviewed = optional(bool, false)
    delivery_verified        = optional(bool, false)
  })
  default = null

  validation {
    condition = var.contact == null ? true : (!var.contact.enabled || (
      var.contact.prepare_secrets && var.contact.provider_reviewed &&
      var.contact.mailbox_cleanup_reviewed && var.contact.delivery_verified
    ))
    error_message = "Contact activation requires prepared secrets, provider review, verified mailbox cleanup and an observed authorized delivery smoke. Flags attest completed checks; they do not perform them."
  }
}

locals {
  contact_enabled = try(var.contact.enabled, false)
  contact_secret_ids = {
    contact_account_id      = "portfolio-${var.environment}-contact-account-id"
    contact_api_token       = "portfolio-${var.environment}-contact-api-token"
    contact_sender_email    = "portfolio-${var.environment}-contact-sender-email"
    contact_recipient_email = "portfolio-${var.environment}-contact-recipient-email"
  }
  contact_prepared_secrets = try(var.contact.prepare_secrets, false) ? local.contact_secret_ids : {}

  # Disabled mode never references a Contact secret version at startup, even
  # after empty containers/accessor bindings were prepared deliberately.
  contact_runtime_secrets = local.contact_enabled ? {
    "Contact__AccountId"      = "contact_account_id"
    "Contact__ApiToken"       = "contact_api_token"
    "Contact__SenderEmail"    = "contact_sender_email"
    "Contact__RecipientEmail" = "contact_recipient_email"
  } : {}
}
