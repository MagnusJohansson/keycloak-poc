# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# ---------------------------------------------------------------------------
# Clients (OIDC "applications").
#
# Rule of thumb this file demonstrates:
#   - Anything running on a device the user controls (browser, phone, desktop)
#     is a PUBLIC client and MUST use authorization-code + PKCE. It cannot keep
#     a secret, so it is not given one.
#   - Anything running on a server you control is CONFIDENTIAL.
#   - An API that never logs anyone in is BEARER-ONLY.
# ---------------------------------------------------------------------------

# --- The resource server ----------------------------------------------------
# Never initiates a login; only validates bearer tokens.
#
# NOTE ON access_type: the intuitive choice here is BEARER-ONLY, and it is wrong.
# Keycloak's Authorization Services are hosted BY the client, which means the
# client must be able to authenticate itself and hold a service account. A
# BEARER-ONLY client can do neither, and Keycloak rejects the combination with a
# bare 500 from POST /admin/realms/{realm}/clients - no useful error message.
#
# So a resource server that uses Authorization Services is CONFIDENTIAL with the
# login flows switched off. It still never initiates a login; it simply also has
# an identity of its own.
resource "keycloak_openid_client" "api" {
  realm_id  = keycloak_realm.docvault.id
  client_id = "docvault-api"
  name      = "DocVault API"
  enabled   = true

  access_type                  = "CONFIDENTIAL"
  service_accounts_enabled     = true  # required by authorization services
  standard_flow_enabled        = false # never starts a browser login
  direct_access_grants_enabled = false

  authorization {
    policy_enforcement_mode = "ENFORCING"
    decision_strategy       = "UNANIMOUS"
  }
}

# --- Browser SPAs -----------------------------------------------------------
locals {
  # Every SPA needs the same shape of config, so express it once.
  spas = {
    "docvault-web-react" = {
      name    = "DocVault Web (React)"
      origins = var.web_react_origins
    }
    "docvault-web-vue" = {
      name    = "DocVault Web (Vue)"
      origins = var.web_vue_origins
    }
  }
}

resource "keycloak_openid_client" "spa" {
  for_each = local.spas

  realm_id  = keycloak_realm.docvault.id
  client_id = each.key
  name      = each.value.name
  enabled   = true

  access_type = "PUBLIC" # no secret: a secret shipped to a browser is not a secret

  standard_flow_enabled        = true  # authorization code
  implicit_flow_enabled        = false # dead since OAuth 2.1 - tokens in the URL fragment leak
  direct_access_grants_enabled = false # no password grant from a browser, ever

  # S256 PKCE. Without this a stolen authorization code can be redeemed by
  # anyone; with it, only the client that generated the verifier can.
  pkce_code_challenge_method = "S256"

  valid_redirect_uris = flatten([
    for origin in each.value.origins : [
      "${origin}/callback",
      "${origin}/silent-renew.html", # hidden iframe for silent token renewal
    ]
  ])
  valid_post_logout_redirect_uris = each.value.origins

  # CORS: the browser will not let the SPA read the token response otherwise.
  web_origins = each.value.origins

  login_theme = "keycloak"
}

# --- Native mobile ----------------------------------------------------------
# One client per app, matching every other app here. Sharing a client would mean
# the two could not be revoked, scoped or audited independently, and `azp` could
# not tell you which app a token came from.
#
# They also get DIFFERENT URI schemes. A custom scheme is claimed OS-wide, so two
# apps registering the same one is ambiguous - Android resolves it
# non-deterministically, iOS generally favours the last installed - and an OAuth
# redirect can be delivered to the wrong app. PKCE makes that non-exploitable,
# but it still breaks sign-in.
locals {
  mobile_clients = {
    "docvault-flutter" = {
      name   = "DocVault Mobile (Flutter)"
      scheme = var.flutter_redirect_scheme
    }
    "docvault-reactnative" = {
      name   = "DocVault Mobile (React Native)"
      scheme = var.react_native_redirect_scheme
    }
  }
}

resource "keycloak_openid_client" "mobile" {
  for_each = local.mobile_clients

  realm_id  = keycloak_realm.docvault.id
  client_id = each.key
  name      = each.value.name
  enabled   = true

  access_type                  = "PUBLIC"
  standard_flow_enabled        = true
  direct_access_grants_enabled = false
  pkce_code_challenge_method   = "S256"

  valid_redirect_uris = [
    "${each.value.scheme}://oauth/callback",
    "${each.value.scheme}://oauth/logout",
  ]
  valid_post_logout_redirect_uris = ["${each.value.scheme}://oauth/logout"]
}

# --- Desktop (Electron) -----------------------------------------------------
# Uses a loopback redirect on an ephemeral port, per RFC 8252. The wildcard port
# is explicitly permitted by Keycloak for exactly this case.
resource "keycloak_openid_client" "desktop" {
  realm_id  = keycloak_realm.docvault.id
  client_id = "docvault-desktop"
  name      = "DocVault Desktop (Electron)"
  enabled   = true

  access_type                  = "PUBLIC"
  standard_flow_enabled        = true
  direct_access_grants_enabled = false
  pkce_code_challenge_method   = "S256"

  # NOT "http://127.0.0.1:*/callback". Keycloak only honours a wildcard at the END
  # of a redirect URI, so a `*` in the port position is matched literally and every
  # authorization request is rejected with `invalid_request` - which looks like a
  # client bug and is not one.
  #
  # "http://127.0.0.1/*" is the form that works: Keycloak applies RFC 8252 loopback
  # handling, ignoring the port for a loopback host, and the trailing wildcard
  # covers the path.
  valid_redirect_uris             = ["http://127.0.0.1/*"]
  valid_post_logout_redirect_uris = ["http://127.0.0.1/*"]
}

# --- Desktop (WinUI 3 / .NET 10) --------------------------------------------
# A second desktop client, identical in protocol terms to the Electron one: both are
# native apps doing authorization code + PKCE against a loopback redirect (RFC 8252).
#
# It gets its OWN client rather than sharing docvault-desktop so the two can be
# revoked, scoped and audited independently - the same reason every other app here
# has one. The redirect URI is byte-identical because the pattern is a property of
# native apps, not of the UI framework.
resource "keycloak_openid_client" "winui" {
  realm_id  = keycloak_realm.docvault.id
  client_id = "docvault-winui"
  name      = "DocVault Desktop (WinUI 3)"
  enabled   = true

  access_type                  = "PUBLIC"
  standard_flow_enabled        = true
  direct_access_grants_enabled = false
  pkce_code_challenge_method   = "S256"

  # See the note on the Electron client above: a wildcard is only honoured at the
  # END of the URI, so the port cannot be wildcarded.
  valid_redirect_uris             = ["http://127.0.0.1/*"]
  valid_post_logout_redirect_uris = ["http://127.0.0.1/*"]
}

# --- Machine-to-machine worker ---------------------------------------------
# No user is ever present. Uses client_credentials and gets its own service
# account identity, which can hold roles just like a human.
resource "keycloak_openid_client" "worker" {
  realm_id  = keycloak_realm.docvault.id
  client_id = "docvault-worker"
  name      = "DocVault Worker (background jobs)"
  enabled   = true

  access_type                  = "CONFIDENTIAL"
  service_accounts_enabled     = true  # <- this is what enables client_credentials
  standard_flow_enabled        = false # a daemon has no browser
  direct_access_grants_enabled = false
}

# --- Analytics client (use-case 5: consent + data minimisation) -------------
resource "keycloak_openid_client" "analytics" {
  realm_id  = keycloak_realm.docvault.id
  client_id = "docvault-analytics"
  name      = "DocVault Analytics"
  enabled   = true

  access_type                  = "CONFIDENTIAL"
  standard_flow_enabled        = true
  direct_access_grants_enabled = false
  pkce_code_challenge_method   = "S256"

  # The user is shown a consent screen and must actively agree before this
  # client receives any claims about them.
  consent_required = true

  valid_redirect_uris = ["${var.api_origin}/analytics/callback"]
}
