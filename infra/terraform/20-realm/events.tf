# ---------------------------------------------------------------------------
# Audit (use-case 6).
#
# Keycloak records two independent event streams. Both are OFF by default,
# which is a common and expensive surprise during an incident review - there is
# simply no history to look at. Turn them on deliberately.
# ---------------------------------------------------------------------------
resource "keycloak_realm_events" "docvault" {
  realm_id = keycloak_realm.docvault.id

  # User-facing events: logins, failures, consents, token refreshes.
  events_enabled    = true
  events_expiration = 604800 # 7 days in the DB; ship to a SIEM for real retention

  # Administrative events: who changed what configuration. This is the stream
  # that answers "who added that identity provider at 2am?".
  admin_events_enabled         = true
  admin_events_details_enabled = true

  enabled_event_types = [
    "LOGIN",
    "LOGIN_ERROR",
    "LOGOUT",
    "REGISTER",
    "UPDATE_PASSWORD",
    "REFRESH_TOKEN",
    "REFRESH_TOKEN_ERROR",
    "CODE_TO_TOKEN",
    "CODE_TO_TOKEN_ERROR",
    "CLIENT_LOGIN",
    "CLIENT_LOGIN_ERROR",
    "GRANT_CONSENT",
    "REVOKE_GRANT",
    "UPDATE_TOTP",
    "REMOVE_TOTP",
  ]

  # jboss-logging writes each event to the server log. In Azure, Container Apps
  # streams that to Log Analytics, which is how events reach Sentinel with no
  # extra component - see docs/use-cases/uc6-audit-siem-sentinel.md. Locally they
  # are readable in the admin console under Realm settings -> Sessions -> Events,
  # and via the Admin REST API.
  events_listeners = ["jboss-logging"]
}
