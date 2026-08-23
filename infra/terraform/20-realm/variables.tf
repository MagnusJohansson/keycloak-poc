variable "keycloak_url" {
  type        = string
  description = "Base URL of the Keycloak instance, no trailing slash. e.g. http://localhost:8080 or https://ca-keycloak.<region>.azurecontainerapps.io"
}

variable "admin_realm" {
  type        = string
  default     = "master"
  description = "Realm holding the administrative account. Always 'master'."
}

variable "admin_client_id" {
  type        = string
  default     = "admin-cli"
  description = "OAuth client used for administration. 'admin-cli' for password auth; your own confidential client id when using client_credentials."
}

variable "admin_username" {
  type        = string
  default     = null
  description = "Admin username. Leave null when authenticating with client_credentials."
}

variable "admin_password" {
  type        = string
  default     = null
  sensitive   = true
  description = "Admin password. Leave null when authenticating with client_credentials."
}

variable "admin_client_secret" {
  type        = string
  default     = null
  sensitive   = true
  description = "Service-account secret for client_credentials. Leave null when using username/password."
}

variable "tls_insecure_skip_verify" {
  type        = bool
  default     = false
  description = "Skip TLS verification. Only ever true for local experiments."
}

variable "realm_name" {
  type    = string
  default = "docvault"
}

# --- Where the client apps live. Differs per environment, hence variables. ---
# --- Where the client apps live. Lists, not single values, on purpose.
#
# A realm usually has to trust more than one origin for the same app: the deployed
# site AND http://localhost while you develop against it. With a single value,
# pointing the realm at a deployed SPA silently revokes local development - which
# is exactly the workflow docs/09-deploying-on-azure.md Step 3 recommends.
variable "web_react_origins" {
  type    = list(string)
  default = ["http://localhost:5173"]
}

variable "web_vue_origins" {
  type    = list(string)
  default = ["http://localhost:5174"]
}

# SINGULAR, unlike the SPA origins above, and it cannot become a list.
#
# The only client using it is docvault-analytics, which carries a pairwise subject
# identifier. Keycloak rejects a pairwise client whose redirect URIs span multiple
# hosts unless a Sector Identifier URI is configured:
#
#   invalid_input: Without a configured Sector Identifier URI, client redirect
#   URIs must not contain multiple host components.
#
# That is the constraint uc5 describes. Point it at whichever API this realm
# actually serves; to support several, publish the sector identifier document
# (the API exposes /analytics/sector-identifier) and set sectorIdentifierUri on
# the mapper.
variable "api_origin" {
  type    = string
  default = "http://localhost:5001"
}

# One scheme PER APP, not one shared between them.
#
# A custom URI scheme is claimed OS-wide. Two apps registering the same one is
# ambiguous: Android picks non-deterministically and iOS generally gives it to
# whichever was installed last, so an OAuth redirect can land in the wrong app.
variable "flutter_redirect_scheme" {
  type        = string
  default     = "io.docvault.flutter"
  description = "Custom URI scheme for the Flutter app."
}

variable "react_native_redirect_scheme" {
  type        = string
  default     = "io.docvault.rn"
  description = "Custom URI scheme for the React Native app."
}

variable "seed_user_password" {
  type        = string
  default     = "DocVaultLab!2026"
  sensitive   = true
  description = "Password for the demo users. MUST satisfy the realm password_policy in realm.tf (length 12) - otherwise Keycloak rejects the seed with invalidPasswordMinLengthMessage. Lab convenience; never ship a shared password."
}

variable "smtp_host" {
  type        = string
  default     = "mailpit"
  description = "SMTP host. 'mailpit' locally; a real relay in production."
}

variable "smtp_port" {
  type    = string
  default = "1025"
}
