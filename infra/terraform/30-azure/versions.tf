# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

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
  features {
    resource_group {
      # Application Insights silently creates a "Failure Anomalies" smart detector
      # alert rule that Terraform never manages. The provider's default guard then
      # refuses to delete the resource group because it "contains resources not
      # managed by Terraform", and `terraform destroy` exits leaving the whole
      # group - and its bill - behind.
      #
      # Safe here because THIS module creates the resource group itself, so there
      # can be nothing in it that the lab did not put there. Do not copy this into
      # a module that deploys into a pre-existing resource group.
      prevent_deletion_if_contains_resources = false
    }
    key_vault {
      purge_soft_delete_on_destroy = true
    }
  }
}
