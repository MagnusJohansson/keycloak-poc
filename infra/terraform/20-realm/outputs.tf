# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# ---------------------------------------------------------------------------
# Everything the apps need in order to talk to this realm.
# `make show-secrets` renders these into .env files.
# ---------------------------------------------------------------------------
output "issuer" {
  description = "OIDC issuer. This exact string appears as `iss` in every token and as `Authority` in the .NET API."
  value       = "${var.keycloak_url}/realms/${keycloak_realm.docvault.realm}"
}

output "discovery_url" {
  description = "OIDC discovery document. curl this first when debugging - if it 404s, nothing else will work."
  value       = "${var.keycloak_url}/realms/${keycloak_realm.docvault.realm}/.well-known/openid-configuration"
}

output "client_ids" {
  description = "Client IDs by application."
  value = {
    api       = keycloak_openid_client.api.client_id
    react     = keycloak_openid_client.spa["docvault-web-react"].client_id
    vue       = keycloak_openid_client.spa["docvault-web-vue"].client_id
    flutter   = keycloak_openid_client.mobile["docvault-flutter"].client_id
    rn        = keycloak_openid_client.mobile["docvault-reactnative"].client_id
    desktop   = keycloak_openid_client.desktop.client_id
    winui     = keycloak_openid_client.winui.client_id
    worker    = keycloak_openid_client.worker.client_id
    analytics = keycloak_openid_client.analytics.client_id
    mcp       = keycloak_openid_client.mcp_client.client_id
  }
}

output "mcp_resource_url" {
  description = "The MCP server's URL: its RFC 8707 resource, its expected `aud`, and its client_id."
  value       = keycloak_openid_client.mcp_server.client_id
}

output "mcp_server_client_secret" {
  description = "The MCP server's secret for token exchange (RFC 8693)."
  value       = keycloak_openid_client.mcp_server.client_secret
  sensitive   = true
}

output "worker_client_secret" {
  description = "client_credentials secret for the background worker."
  value       = keycloak_openid_client.worker.client_secret
  sensitive   = true
}

output "analytics_client_secret" {
  value     = keycloak_openid_client.analytics.client_secret
  sensitive = true
}

output "demo_users" {
  description = "Seeded users and what each one demonstrates."
  value = {
    alice = "acme/engineering - doc.reader + doc.editor. The happy path."
    bob   = "globex - doc.reader. Proves tenant isolation: cannot see Acme documents."
    carol = "acme/legal - doc.admin + platform-admin. Needs OTP step-up for classified."
    dave  = "no groups. Authenticated but unauthorized -> 403, not 401."
  }
}

# --- For the legacy SAML application (apps/saml-reports). `make reports` reads these. ---
output "saml_idp_certificate" {
  description = "The realm's signing certificate. The application pins it."
  value       = data.keycloak_realm_keys.signing.keys[0].certificate
}

output "reports_saml_signing_certificate" {
  value = tls_self_signed_cert.reports.cert_pem
}

output "reports_saml_signing_key" {
  value     = tls_private_key.reports.private_key_pem
  sensitive = true
}

output "api_directory_client_secret" {
  description = "The API's secret for looking people up to share a document with (directory.tf)."
  value       = keycloak_openid_client.api_directory.client_secret
  sensitive   = true
}
