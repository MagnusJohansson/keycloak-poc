# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# ---------------------------------------------------------------------------
# Keycloak on Azure Container Apps.
# ---------------------------------------------------------------------------
resource "azurerm_log_analytics_workspace" "kc" {
  name                = "log-keycloak"
  resource_group_name = azurerm_resource_group.kc.name
  location            = azurerm_resource_group.kc.location
  sku                 = "PerGB2018"
  retention_in_days   = 30
  tags                = var.tags
}

resource "azurerm_container_app_environment" "kc" {
  name                       = "cae-keycloak"
  resource_group_name        = azurerm_resource_group.kc.name
  location                   = azurerm_resource_group.kc.location
  log_analytics_workspace_id = azurerm_log_analytics_workspace.kc.id

  # Joins the VNet, which is what lets Keycloak reach a database that has no
  # public endpoint.
  infrastructure_subnet_id = azurerm_subnet.container_apps.id

  tags = var.tags

  lifecycle {
    # Azure materialises the default "Consumption" workload profile server-side.
    # Terraform did not declare it, so every plan would offer to delete it.
    ignore_changes = [workload_profile]
  }
}

locals {
  app_name = "ca-keycloak"

  # The public hostname, computed BEFORE the app exists.
  #
  # This matters: KC_HOSTNAME must be the externally visible URL, but the app's
  # own FQDN is only known after it is created - a chicken-and-egg that usually
  # forces a two-pass apply. `default_domain` is an attribute of the
  # ENVIRONMENT, which is created first, so the FQDN can be derived here and
  # passed in on the first pass.
  default_fqdn = "${local.app_name}.${azurerm_container_app_environment.kc.default_domain}"
  hostname     = var.custom_domain != null ? var.custom_domain : local.default_fqdn
  issuer_base  = "https://${local.hostname}"

  # jdbc:postgresql://<private fqdn>:5432/keycloak?sslmode=require
  # sslmode=require is not optional: Azure Postgres rejects unencrypted
  # connections, and the failure looks like a generic connection error.
  jdbc_url = "jdbc:postgresql://${azurerm_postgresql_flexible_server.kc.fqdn}:5432/${azurerm_postgresql_flexible_server_database.keycloak.name}?sslmode=require"

  image = coalesce(var.keycloak_image, "quay.io/keycloak/keycloak:${var.keycloak_version}")
}

resource "azurerm_container_app" "keycloak" {
  name                         = local.app_name
  resource_group_name          = azurerm_resource_group.kc.name
  container_app_environment_id = azurerm_container_app_environment.kc.id
  revision_mode                = "Single"
  tags                         = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.keycloak.id]
  }

  # Resolved from Key Vault at runtime using the identity above - the secret
  # values themselves never appear in the container app definition.
  secret {
    name                = "db-password"
    identity            = azurerm_user_assigned_identity.keycloak.id
    key_vault_secret_id = azurerm_key_vault_secret.postgres_password.versionless_id
  }

  secret {
    name                = "admin-password"
    identity            = azurerm_user_assigned_identity.keycloak.id
    key_vault_secret_id = azurerm_key_vault_secret.admin_password.versionless_id
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
    min_replicas = var.min_replicas
    max_replicas = var.max_replicas

    container {
      name   = "keycloak"
      image  = local.image
      cpu    = 1.0
      memory = "2Gi"

      # `start`, never `start-dev`. Dev mode disables hostname and HTTPS checks
      # and would happily run in production while silently weakening both.
      command = ["/opt/keycloak/bin/kc.sh"]
      args    = var.keycloak_image != null ? ["start", "--optimized"] : ["start"]

      # --- Database ---
      env {
        name  = "KC_DB"
        value = "postgres"
      }
      env {
        name  = "KC_DB_URL"
        value = local.jdbc_url
      }
      env {
        name  = "KC_DB_USERNAME"
        value = azurerm_postgresql_flexible_server.kc.administrator_login
      }
      env {
        name        = "KC_DB_PASSWORD"
        secret_name = "db-password"
      }

      # --- Hostname and proxy ---
      # Container Apps terminates TLS at the edge and forwards plain HTTP, so
      # Keycloak must (a) accept HTTP on the container port and (b) trust the
      # X-Forwarded-* headers to build correct absolute URLs. Get this wrong and
      # Keycloak issues redirect and issuer URLs on http://<internal-name>,
      # which breaks every OIDC client with an "invalid redirect" or an issuer
      # mismatch that is maddening to trace.
      env {
        name  = "KC_HOSTNAME"
        value = local.issuer_base
      }
      env {
        name  = "KC_HTTP_ENABLED"
        value = "true"
      }
      env {
        name  = "KC_PROXY_HEADERS"
        value = "xforwarded"
      }

      # --- Clustering ---
      # jdbc-ping is the default from Keycloak 26.1, but stated explicitly
      # because it is the setting that makes multi-replica work here: nodes
      # discover each other through the database, so no multicast is needed.
      env {
        name  = "KC_CACHE"
        value = "ispn"
      }
      env {
        name  = "KC_CACHE_STACK"
        value = "jdbc-ping"
      }

      # --- Health and telemetry (management port 9000) ---
      env {
        name  = "KC_HEALTH_ENABLED"
        value = "true"
      }
      env {
        name  = "KC_METRICS_ENABLED"
        value = "true"
      }

      # --- Bootstrap admin: consumed only on an empty database ---
      env {
        name  = "KC_BOOTSTRAP_ADMIN_USERNAME"
        value = var.admin_username
      }
      env {
        name        = "KC_BOOTSTRAP_ADMIN_PASSWORD"
        secret_name = "admin-password"
      }

      # Health endpoints live on the management port, not the ingress port.
      startup_probe {
        transport = "HTTP"
        port      = 9000
        path      = "/health/started"
        # Generous: an unoptimized image runs `kc.sh build` on first start.
        failure_count_threshold = 30
        interval_seconds        = 10
      }

      liveness_probe {
        transport        = "HTTP"
        port             = 9000
        path             = "/health/live"
        interval_seconds = 30
      }

      readiness_probe {
        transport        = "HTTP"
        port             = 9000
        path             = "/health/ready"
        interval_seconds = 10
      }
    }
  }

  depends_on = [
    azurerm_role_assignment.keycloak_kv_user,
    azurerm_postgresql_flexible_server_database.keycloak,
  ]

  lifecycle {
    # Azure sets this to "Consumption" itself; see the environment above.
    ignore_changes = [workload_profile_name]
  }
}
