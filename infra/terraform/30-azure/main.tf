# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

data "azurerm_client_config" "current" {}

resource "azurerm_resource_group" "lab" {
  name     = var.resource_group_name
  location = var.location
  tags     = var.tags
}

# --- Observability + the Sentinel target (use-case 6) -----------------------
resource "azurerm_log_analytics_workspace" "lab" {
  name                = "log-docvault"
  resource_group_name = azurerm_resource_group.lab.name
  location            = azurerm_resource_group.lab.location
  sku                 = "PerGB2018"
  retention_in_days   = 30
  tags                = var.tags
}

# Turns the workspace into a SIEM. Container Apps streams Keycloak's stdout -
# including the events emitted by its jboss-logging listener - into Log
# Analytics, so realm security events land beside your application telemetry and
# can be correlated either way. Sentinel adds analytics rules, incidents and
# hunting on top. See docs/use-cases/uc6-audit-siem-sentinel.md.
#
# Opt-in: Sentinel bills per GB analysed, and a lab someone clones should not
# start metered billing without being asked. -var="enable_sentinel=true".
resource "azurerm_sentinel_log_analytics_workspace_onboarding" "lab" {
  count = var.enable_sentinel ? 1 : 0

  workspace_id = azurerm_log_analytics_workspace.lab.id
}

resource "azurerm_application_insights" "api" {
  name                = "appi-docvault-api"
  resource_group_name = azurerm_resource_group.lab.name
  location            = azurerm_resource_group.lab.location
  workspace_id        = azurerm_log_analytics_workspace.lab.id
  application_type    = "web"
  tags                = var.tags
}

# --- Identity ---------------------------------------------------------------
# A user-assigned identity so the API can read Key Vault without ever holding a
# credential. This is the Azure-side counterpart to Keycloak service accounts:
# both replace a shared secret with a platform-issued, rotatable identity.
resource "azurerm_user_assigned_identity" "api" {
  name                = "id-docvault-api"
  resource_group_name = azurerm_resource_group.lab.name
  location            = azurerm_resource_group.lab.location
  tags                = var.tags
}

resource "azurerm_key_vault" "lab" {
  name                       = "kv-docvault-${substr(sha256(azurerm_resource_group.lab.id), 0, 8)}"
  resource_group_name        = azurerm_resource_group.lab.name
  location                   = azurerm_resource_group.lab.location
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true  # RBAC over legacy access policies
  purge_protection_enabled   = false # lab only, so the vault can be torn down
  tags                       = var.tags
}

resource "azurerm_role_assignment" "api_kv_reader" {
  scope                = azurerm_key_vault.lab.id
  role_definition_name = "Key Vault Secrets User" # read secret values, nothing else
  principal_id         = azurerm_user_assigned_identity.api.principal_id
}

# --- Container registry -----------------------------------------------------
# Holds the API image. Created here rather than by hand so the whole deployment
# stays reproducible - but note the bootstrap order in
# docs/09-deploying-on-azure.md: the registry must exist and hold the image
# BEFORE the container app referencing it is created.
resource "azurerm_container_registry" "acr" {
  name                = "acrdocvault${random_string.suffix.result}"
  resource_group_name = azurerm_resource_group.lab.name
  location            = azurerm_resource_group.lab.location
  sku                 = "Basic"

  # No admin user. It is a shared username/password that cannot be scoped or
  # attributed to anyone; the managed identity below replaces it entirely.
  admin_enabled = false

  tags = var.tags
}

resource "random_string" "suffix" {
  length  = 6
  special = false
  upper   = false
}

# AcrPull, not Contributor: the app only ever reads images.
resource "azurerm_role_assignment" "api_acr_pull" {
  scope                = azurerm_container_registry.acr.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.api.principal_id
}

# --- The API ----------------------------------------------------------------
resource "azurerm_container_app_environment" "lab" {
  name                       = "cae-docvault"
  resource_group_name        = azurerm_resource_group.lab.name
  location                   = azurerm_resource_group.lab.location
  log_analytics_workspace_id = azurerm_log_analytics_workspace.lab.id
  tags                       = var.tags

  lifecycle {
    # Azure materialises the default "Consumption" workload profile server-side.
    # Terraform did not declare it, so every plan would offer to delete it.
    ignore_changes = [workload_profile]
  }
}

# Created only once container_image is supplied.
#
# This is what removes the chicken-and-egg: the registry must exist and hold an
# image before anything can reference it, so the first apply creates everything
# EXCEPT this, you push, then a second apply adds it. No `-target` needed - and
# `-target` would not have worked anyway, since Terraform still requires every
# variable when targeting.
resource "azurerm_container_app" "api" {
  count = var.container_image != null ? 1 : 0

  name                         = "ca-docvault-api"
  resource_group_name          = azurerm_resource_group.lab.name
  container_app_environment_id = azurerm_container_app_environment.lab.id
  revision_mode                = "Single"
  tags                         = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.api.id]
  }

  # Without this the app cannot authenticate to a private registry and the
  # revision fails with an image-pull error. `identity` points at the
  # user-assigned identity granted AcrPull above - no registry password anywhere.
  registry {
    server   = azurerm_container_registry.acr.login_server
    identity = azurerm_user_assigned_identity.api.id
  }

  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = 1
    max_replicas = 3

    container {
      name   = "api"
      image  = var.container_image
      cpu    = 0.5
      memory = "1Gi"

      # The ONE value that differs between the local lab and production. The API
      # code is identical; only the issuer it trusts changes.
      env {
        name  = "Keycloak__Authority"
        value = var.keycloak_issuer
      }

      # appsettings.json ships RequireHttpsMetadata = false, which is correct for
      # the loopback default but must be overridden here. The API has a startup
      # guard that REFUSES to run with a non-loopback authority over plain HTTP -
      # so without this the container exits immediately with ActivationFailed.
      # That is the guard working, not a bug: fetching signing keys unencrypted
      # from a remote host would let an attacker mint valid tokens.
      env {
        name  = "Keycloak__RequireHttpsMetadata"
        value = "true"
      }
      env {
        name  = "Keycloak__Audience"
        value = "docvault-api"
      }
      env {
        name  = "APPLICATIONINSIGHTS_CONNECTION_STRING"
        value = azurerm_application_insights.api.connection_string
      }
      env {
        name  = "AZURE_CLIENT_ID"
        value = azurerm_user_assigned_identity.api.client_id
      }

      liveness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/health" # the anonymous endpoint - a probe has no token
      }
    }
  }

  # The pull grant must exist before the first revision starts, or it fails to
  # pull and the apply reports a confusing timeout instead of a permissions error.
  depends_on = [azurerm_role_assignment.api_acr_pull]

  lifecycle {
    # Azure sets this to "Consumption" itself; see the environment above.
    ignore_changes = [workload_profile_name]

    precondition {
      condition     = var.keycloak_issuer != null
      error_message = "keycloak_issuer must be set when container_image is. Without it the API has no authority to validate tokens against and fails at startup."
    }
  }
}

# --- The SPAs ---------------------------------------------------------------
resource "azurerm_static_web_app" "react" {
  name                = "swa-docvault-react"
  resource_group_name = azurerm_resource_group.lab.name

  # NOT azurerm_resource_group.lab.location - see the variable's comment.
  location = var.static_web_app_location
  sku_tier = "Free"
  sku_size = "Free"
  tags     = var.tags
}

resource "azurerm_static_web_app" "vue" {
  name                = "swa-docvault-vue"
  resource_group_name = azurerm_resource_group.lab.name
  location            = var.static_web_app_location
  sku_tier            = "Free"
  sku_size            = "Free"
  tags                = var.tags
}
