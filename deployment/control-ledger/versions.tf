terraform {
  required_version = ">= 1.5.0"

  required_providers {
    google = {
      source  = "hashicorp/google"
      version = "~> 6.0"
    }
  }
}

# The provider authenticates from the operator's application default credentials. There is
# deliberately no credential in this configuration: a root variable, even a sensitive one, is
# written into saved plan files. See docs/runbooks/model-control.md.
provider "google" {
  project = var.project_id
}
