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
variable "web_react_origin" {
  type    = string
  default = "http://localhost:5173"
}

variable "web_vue_origin" {
  type    = string
  default = "http://localhost:5174"
}

variable "api_origin" {
  type    = string
  default = "http://localhost:5001"
}

variable "mobile_redirect_scheme" {
  type        = string
  default     = "io.docvault.app"
  description = "Custom URI scheme for the Flutter / React Native apps."
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
