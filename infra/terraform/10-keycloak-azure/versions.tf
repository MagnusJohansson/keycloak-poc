# ---------------------------------------------------------------------------
# 10-keycloak-azure : Keycloak running in your Azure subscription.
#
# The cloud counterpart to infra/local/docker-compose.yml. Both run the same
# upstream Keycloak, so ../20-realm applies to either without modification -
# which is what makes the free local lab a faithful rehearsal for this one
# rather than a simplified toy.
#
# Deploys Keycloak on Azure Container Apps with a private Postgres Flexible
# Server, secrets in Key Vault reached by managed identity, and no public
# database endpoint.
#
# What you take on by running this: upgrades, CVE response for Keycloak and the
# JVM, backups you have actually restored, HA across zones, and being on call -
# an authentication outage is a total outage. See docs/09-deploying-on-azure.md
# for the full production-hardening checklist.
# ---------------------------------------------------------------------------
terraform {
  required_version = ">= 1.9"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
}

provider "azurerm" {
  features {
    key_vault {
      # Lab convenience: let `terraform destroy` actually remove the vault
      # instead of leaving a soft-deleted name that blocks re-creation.
      purge_soft_delete_on_destroy = true
    }
  }
}
