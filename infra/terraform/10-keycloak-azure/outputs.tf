output "keycloak_url" {
  description = "Base URL. Feed into ../20-realm's `keycloak_url` - the same variable the local Docker URL goes into."
  value       = local.issuer_base
}

output "issuer" {
  description = "OIDC issuer for the docvault realm, once 20-realm has been applied."
  value       = "${local.issuer_base}/realms/docvault"
}

output "admin_console_url" {
  value = "${local.issuer_base}/admin"
}

output "admin_username" {
  value = var.admin_username
}

output "admin_password_secret" {
  description = "Read it with: az keyvault secret show --vault-name <vault> --name keycloak-bootstrap-admin-password --query value -o tsv"
  value = {
    vault  = azurerm_key_vault.kc.name
    secret = azurerm_key_vault_secret.admin_password.name
  }
}

output "postgres_fqdn" {
  description = "Private only - resolvable inside the VNet, not from your laptop."
  value       = azurerm_postgresql_flexible_server.kc.fqdn
}

# Renders a ready-to-use tfvars for 20-realm so no secret is transcribed by hand.
output "realm_tfvars" {
  description = "make azure-apply (writes infra/environments/azure/realm.tfvars)"
  sensitive   = true
  value       = <<-EOT
    keycloak_url    = "${local.issuer_base}"
    admin_client_id = "admin-cli"
    admin_username  = "${var.admin_username}"
    admin_password  = "${random_password.admin.result}"

    web_react_origins = ["http://localhost:5173"]
    web_vue_origins   = ["http://localhost:5174"]
    api_origin        = "http://localhost:5001"
  EOT
}
