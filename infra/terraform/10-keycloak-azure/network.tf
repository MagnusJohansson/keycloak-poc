# ---------------------------------------------------------------------------
# Private networking.
#
# The database is NOT reachable from the internet. The obvious shortcut -
# public access plus the "allow all Azure services" firewall rule
# (0.0.0.0/0.0.0.0) - is genuinely dangerous: that rule admits every Azure
# tenant, not just yours. A VNet with two delegated subnets costs a few more
# lines and removes the internet from the picture entirely.
# ---------------------------------------------------------------------------
resource "azurerm_resource_group" "kc" {
  name     = var.resource_group_name
  location = var.location
  tags     = var.tags
}

resource "azurerm_virtual_network" "kc" {
  name                = "vnet-keycloak"
  resource_group_name = azurerm_resource_group.kc.name
  location            = azurerm_resource_group.kc.location
  address_space       = ["10.20.0.0/16"]
  tags                = var.tags
}

# Container Apps requires a dedicated subnet of at least /23.
resource "azurerm_subnet" "container_apps" {
  name                 = "snet-container-apps"
  resource_group_name  = azurerm_resource_group.kc.name
  virtual_network_name = azurerm_virtual_network.kc.name
  address_prefixes     = ["10.20.0.0/23"]

  delegation {
    name = "container-apps"
    service_delegation {
      name    = "Microsoft.App/environments"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}

# Postgres Flexible Server with VNet integration needs its own delegated subnet
# and will not share one.
resource "azurerm_subnet" "postgres" {
  name                 = "snet-postgres"
  resource_group_name  = azurerm_resource_group.kc.name
  virtual_network_name = azurerm_virtual_network.kc.name
  address_prefixes     = ["10.20.2.0/24"]

  delegation {
    name = "postgres"
    service_delegation {
      name    = "Microsoft.DBforPostgreSQL/flexibleServers"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}

# Resolves the server's FQDN to its private address inside the VNet. Without
# this the JDBC URL resolves to a public IP that is not listening.
resource "azurerm_private_dns_zone" "postgres" {
  name                = "${azurerm_resource_group.kc.name}.postgres.database.azure.com"
  resource_group_name = azurerm_resource_group.kc.name
  tags                = var.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "postgres" {
  name                  = "keycloak-postgres"
  resource_group_name   = azurerm_resource_group.kc.name
  private_dns_zone_name = azurerm_private_dns_zone.postgres.name
  virtual_network_id    = azurerm_virtual_network.kc.id
  registration_enabled  = false
  tags                  = var.tags
}
