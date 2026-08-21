# ---------------------------------------------------------------------------
# The database.
#
# Keycloak uses Postgres for two things now, not one:
#   1. its own data (realms, users, sessions)
#   2. CLUSTER DISCOVERY - since Keycloak 26.1 the default cache stack is
#      `jdbc-ping`, so nodes find each other through a database table rather
#      than UDP multicast.
#
# That second point is what makes Container Apps viable at all. Multicast is
# unavailable on Azure PaaS, and it used to be the reason clustered Keycloak
# effectively required AKS with KUBE_PING.
#
# It also means the database is a hard dependency for HA, not merely for
# storage: if Postgres is unavailable, the cluster cannot form.
# ---------------------------------------------------------------------------
locals {
  postgres_admin = "kcadmin"
}

resource "random_string" "suffix" {
  length  = 6
  special = false
  upper   = false
}

resource "random_password" "postgres" {
  length  = 32
  special = true
  # Restricted on purpose: characters like @ : / ? & terminate or escape a JDBC
  # URL, and the resulting failure surfaces as an authentication error rather
  # than a parsing one - which sends you looking in entirely the wrong place.
  override_special = "-_=+"
}

resource "azurerm_postgresql_flexible_server" "kc" {
  name                = "psql-keycloak-${random_string.suffix.result}"
  resource_group_name = azurerm_resource_group.kc.name
  location            = azurerm_resource_group.kc.location

  version    = "16"
  sku_name   = var.postgres_sku
  storage_mb = var.postgres_storage_mb

  administrator_login    = local.postgres_admin
  administrator_password = random_password.postgres.result

  # No public endpoint at all: reachable only from inside the VNet.
  public_network_access_enabled = false
  delegated_subnet_id           = azurerm_subnet.postgres.id
  private_dns_zone_id           = azurerm_private_dns_zone.postgres.id

  backup_retention_days        = 7
  geo_redundant_backup_enabled = false

  # High availability is NOT supported on the Burstable (B-series) tier, and
  # requesting it there fails at apply with an unhelpful error. The block is
  # therefore emitted only when HA was actually asked for - which the variable
  # validation below ties to a General Purpose or Memory Optimized SKU.
  dynamic "high_availability" {
    for_each = var.postgres_zone_redundant ? [1] : []
    content {
      mode = "ZoneRedundant"
    }
  }

  tags = var.tags

  depends_on = [azurerm_private_dns_zone_virtual_network_link.postgres]

  lifecycle {
    # Azure assigns the availability zone. Without this, an unrelated change can
    # surface a zone difference as drift and propose replacing the database.
    ignore_changes = [zone]

    precondition {
      condition     = !var.postgres_zone_redundant || !startswith(var.postgres_sku, "B_")
      error_message = "High availability is not available on the Burstable tier. Set postgres_sku to a GP_ or MO_ SKU (e.g. GP_Standard_D2ds_v5), or leave postgres_zone_redundant = false."
    }
  }
}

resource "azurerm_postgresql_flexible_server_database" "keycloak" {
  name      = "keycloak"
  server_id = azurerm_postgresql_flexible_server.kc.id
  charset   = "UTF8"
  collation = "en_US.utf8"

  # NOTE: no `prevent_destroy` here, deliberately.
  #
  # This is a lab that people need to be able to tear down, and everything in it
  # is reproducible: 20-realm recreates the entire realm, and the only users are
  # seeded demo accounts. For a real deployment add
  #   lifecycle { prevent_destroy = true }
  # because there the database holds the one thing you cannot re-run Terraform
  # to recover - your actual users.
}
