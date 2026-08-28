# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# ---------------------------------------------------------------------------
# Secrets, reached by managed identity.
#
# The Container App holds no credential of its own: it authenticates to Key
# Vault as its user-assigned identity and resolves secret references at
# runtime. Same principle as the worker's client_credentials grant - replace a
# standing secret with a short-lived, platform-issued token.
# ---------------------------------------------------------------------------
data "azurerm_client_config" "current" {}

resource "azurerm_user_assigned_identity" "keycloak" {
  name                = "id-keycloak"
  resource_group_name = azurerm_resource_group.kc.name
  location            = azurerm_resource_group.kc.location
  tags                = var.tags
}

resource "azurerm_key_vault" "kc" {
  name                       = "kv-kc-${random_string.suffix.result}"
  resource_group_name        = azurerm_resource_group.kc.name
  location                   = azurerm_resource_group.kc.location
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true
  soft_delete_retention_days = 7
  purge_protection_enabled   = false # lab only; enable for production

  # Left on the public endpoint (guarded by RBAC) so `terraform apply` can write
  # the secrets below from a laptop or CI runner. For production, restrict it to
  # your VNet with a private endpoint and run Terraform from inside that network
  # - otherwise the deployer loses the ability to manage its own secrets.
  public_network_access_enabled = true
  tags                          = var.tags
}

# The identity running Terraform needs write access to create the secrets below.
resource "azurerm_role_assignment" "deployer_kv_officer" {
  scope                = azurerm_key_vault.kc.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = data.azurerm_client_config.current.object_id
}

# Keycloak only needs to read them.
resource "azurerm_role_assignment" "keycloak_kv_user" {
  scope                = azurerm_key_vault.kc.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_user_assigned_identity.keycloak.principal_id
}

resource "azurerm_key_vault_secret" "postgres_password" {
  name         = "keycloak-db-password"
  value        = random_password.postgres.result
  key_vault_id = azurerm_key_vault.kc.id

  depends_on = [azurerm_role_assignment.deployer_kv_officer]
}

# ---------------------------------------------------------------------------
# The bootstrap admin.
#
# KC_BOOTSTRAP_ADMIN_* creates the temporary admin ONLY on an empty database.
# Once the realm exists, ../20-realm should authenticate as a service account
# instead, and this account should be deleted. Leaving a permanent password
# admin on the master realm is how self-hosted Keycloak installations get
# compromised.
# ---------------------------------------------------------------------------
resource "random_password" "admin" {
  length           = 32
  special          = true
  override_special = "-_=+"
}

resource "azurerm_key_vault_secret" "admin_password" {
  name         = "keycloak-bootstrap-admin-password"
  value        = random_password.admin.result
  key_vault_id = azurerm_key_vault.kc.id

  depends_on = [azurerm_role_assignment.deployer_kv_officer]
}
