output "api_url" {
  description = "Feed into the SPAs' VITE_API_BASE_URL, and into 20-realm's api_origin."
  value       = "https://${azurerm_container_app.api.ingress[0].fqdn}"
}

output "react_url" {
  description = "Add to 20-realm's web_react_origin so Keycloak accepts it as a redirect URI."
  value       = "https://${azurerm_static_web_app.react.default_host_name}"
}

output "vue_url" {
  value = "https://${azurerm_static_web_app.vue.default_host_name}"
}

output "sentinel_workspace_id" {
  description = "Workspace collecting Keycloak events and app telemetry (use-case 6)."
  value       = azurerm_log_analytics_workspace.lab.workspace_id
}

output "key_vault_name" {
  value = azurerm_key_vault.lab.name
}
