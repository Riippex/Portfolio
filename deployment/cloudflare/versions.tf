terraform {
  required_version = ">= 1.5.0"

  required_providers {
    cloudflare = {
      source  = "cloudflare/cloudflare"
      version = "~> 4.40"
    }
  }
}

# The provider authenticates from the CLOUDFLARE_API_TOKEN environment variable of the
# operator's shell. There is deliberately no credential in this configuration: a root
# variable, even a sensitive one, is written into saved plan files, while an environment
# variable never reaches the plan. See docs/runbooks/infrastructure.md.
provider "cloudflare" {}
