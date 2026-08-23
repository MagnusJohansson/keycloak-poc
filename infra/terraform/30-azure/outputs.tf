output "acr_login_server" {
  description = "Push the API image here: az acr build --registry <name> --image docvault-api:<tag> apps/api-dotnet"
  value       = azurerm_container_registry.acr.login_server
}

output "acr_name" {
  value = azurerm_container_registry.acr.name
}

output "api_url" {
  description = "Feed into the SPAs' VITE_API_BASE_URL, and into 20-realm's api_origin."
  value       = try("https://${azurerm_container_app.api[0].ingress[0].fqdn}", null)
}

output "react_url" {
  description = "Add to 20-realm's web_react_origin so Keycloak accepts it as a redirect URI."
  value       = "https://${azurerm_static_web_app.react.default_host_name}"
}

output "vue_url" {
  value = "https://${azurerm_static_web_app.vue.default_host_name}"
}

output "log_analytics_workspace_id" {
  description = "Workspace collecting Keycloak events and app telemetry. Onboarded to Sentinel only when enable_sentinel = true (use-case 6)."
  value       = azurerm_log_analytics_workspace.lab.workspace_id
}

output "key_vault_name" {
  value = azurerm_key_vault.lab.name
}
