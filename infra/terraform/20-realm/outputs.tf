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
    mobile    = keycloak_openid_client.mobile.client_id
    desktop   = keycloak_openid_client.desktop.client_id
    worker    = keycloak_openid_client.worker.client_id
    analytics = keycloak_openid_client.analytics.client_id
  }
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
