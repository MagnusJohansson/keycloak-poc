# ---------------------------------------------------------------------------
# 30-azure : YOUR applications, on Azure, alongside Keycloak.
#
# Apply AFTER ../10-keycloak-azure (which stands Keycloak up) and ../20-realm
# (which configures it). This module only deploys the DocVault API and the SPAs,
# plus the shared observability and secret plumbing.
#
# The Azure-specific identity work lives here:
#   - Entra ID federated into Keycloak as an upstream IdP   (use-case 2)
#   - Keycloak events into Log Analytics / Sentinel         (use-case 6)
#   - secrets in Key Vault, reached via managed identity - none in config
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
  features {}
}
