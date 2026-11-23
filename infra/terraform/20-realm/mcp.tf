# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# ---------------------------------------------------------------------------
# MCP server (apps/mcp-dotnet): an AI assistant's door into DocVault.
#
# Three parties, three objects here:
#   - the MCP *server* is an OAuth resource server (keycloak_openid_client.mcp_server);
#   - the MCP *client* (Claude Code, an IDE, ...) signs the user in
#     (keycloak_openid_client.mcp_client);
#   - what the assistant may do is a set of optional scopes the user consents to.
#
# NEEDS the Keycloak feature `resource-indicators` (experimental in 26.6; see
# infra/local/docker-compose.yml). Without it Keycloak ignores the `resource`
# parameter MCP clients must send (RFC 8707), and the audience below is not the
# server's URL.
# ---------------------------------------------------------------------------

# --- The MCP server --------------------------------------------------------
# NOTE ON client_id: it is the server's URL, on purpose.
#
# With resource indicators on, a token requested with `resource=<url>` carries
# exactly that URL as `aud`. The server then exchanges the token for one aimed
# at the API (RFC 8693), and Keycloak's standard token exchange only lets a
# client exchange a token whose `aud` contains that client's *client_id*. A
# client called "docvault-mcp" is therefore refused with
#   403 access_denied "Client is not within the token audience"
# Naming the client after its resource URL makes the two agree. Verified
# against Keycloak 26.6.3; do not "tidy" it into a short name.
resource "keycloak_openid_client" "mcp_server" {
  realm_id  = keycloak_realm.docvault.id
  client_id = var.mcp_resource_url
  name      = "DocVault MCP server"
  enabled   = true

  # Confidential: it authenticates to the token endpoint for the exchange.
  # It never signs anyone in and has no identity of its own to act with.
  access_type                  = "CONFIDENTIAL"
  standard_flow_enabled        = false
  direct_access_grants_enabled = false
  service_accounts_enabled     = false

  standard_token_exchange_enabled = true

  # The exchanged token may carry the API's document roles and nothing else.
  # With full scope it would also carry realm roles such as platform-admin,
  # and an assistant acting for an administrator could reach /admin.
  full_scope_allowed = false

  extra_config = {
    # What `resource=` must match (RFC 8707).
    "resource_url" = var.mcp_resource_url
  }
}

resource "keycloak_generic_role_mapper" "mcp_server_api_roles" {
  for_each = keycloak_role.api

  realm_id  = keycloak_realm.docvault.id
  client_id = keycloak_openid_client.mcp_server.id
  role_id   = each.value.id
}

# The exchanged token is for the API, so it needs what every API caller's
# token has: the API as audience, and the tenant group path.
#
# The audience mapper is not redundant with the roles above. A user with no
# document roles (dave) would otherwise be refused at the exchange with
# "Requested audience not available"; with it the API answers its usual 403.
resource "keycloak_openid_audience_protocol_mapper" "mcp_server_api_audience" {
  realm_id  = keycloak_realm.docvault.id
  client_id = keycloak_openid_client.mcp_server.id
  name      = "docvault-api-audience"

  included_client_audience = keycloak_openid_client.api.client_id
  add_to_access_token      = true
  add_to_id_token          = false
}

resource "keycloak_openid_group_membership_protocol_mapper" "mcp_server_groups" {
  realm_id  = keycloak_realm.docvault.id
  client_id = keycloak_openid_client.mcp_server.id
  name      = "groups"

  claim_name          = "groups"
  full_path           = true
  add_to_id_token     = false
  add_to_access_token = true
  add_to_userinfo     = false
}

# --- What the assistant may do ----------------------------------------------
# Optional scopes: granted only when the client asks AND the user consents.
# They limit the *assistant*; the API's roles still limit the *user*. A call
# succeeds only where both allow it.
locals {
  mcp_scopes = {
    "docvault:read"   = "Let the assistant list your documents"
    "docvault:write"  = "Let the assistant create documents"
    "docvault:delete" = "Let the assistant delete documents"
    # Its own scope: a share is how a document reaches people who could not see
    # it, the thing a prompt injection would most want done. Asked for only when
    # the share tool is first used, so the user sees this line on its own.
    "docvault:share" = "Let the assistant share your documents with people in your organisation"
  }
}

resource "keycloak_openid_client_scope" "mcp" {
  for_each = local.mcp_scopes

  realm_id               = keycloak_realm.docvault.id
  name                   = each.key
  description            = "MCP: ${each.value}"
  include_in_token_scope = true
  consent_screen_text    = each.value
}

# --- The MCP client ----------------------------------------------------------
# Pre-registered and public: the same shape as the desktop clients. MCP's
# preferred registration (Client ID Metadata Documents) is experimental in
# Keycloak 26.6 and not used here; dynamic registration is deprecated by MCP.
resource "keycloak_openid_client" "mcp_client" {
  realm_id  = keycloak_realm.docvault.id
  client_id = "docvault-mcp-client"
  name      = "DocVault MCP client (AI assistant)"
  enabled   = true

  access_type                  = "PUBLIC"
  standard_flow_enabled        = true
  direct_access_grants_enabled = false
  pkce_code_challenge_method   = "S256"

  # The user sees what the assistant is asking for, and can refuse.
  consent_required = true

  # Its tokens are for the MCP server only; they carry no roles at all.
  full_scope_allowed = false

  # MCP clients listen on a random loopback port. Keycloak ignores the port
  # for loopback hosts (RFC 8252); see the desktop clients in clients.tf.
  valid_redirect_uris = ["http://127.0.0.1/*", "http://localhost/*"]
}

# Without `resource` (a client that ignores RFC 8707) the token's audience is
# this mapper's: the server's client_id, which is the same URL. With it,
# Keycloak narrows the audience to the requested resource. Either way the
# server sees its own URL, and a token for anything else is refused.
resource "keycloak_openid_audience_protocol_mapper" "mcp_client_audience" {
  realm_id  = keycloak_realm.docvault.id
  client_id = keycloak_openid_client.mcp_client.id
  name      = "docvault-mcp-audience"

  included_client_audience = keycloak_openid_client.mcp_server.client_id
  add_to_access_token      = true
  add_to_id_token          = false
}

# The consent screen lists every scope attached to the client, so the built-in
# `roles` scope showed as "User roles" although this client's token carries no
# roles (full scope is off). Dropping it makes the screen say what is granted.
#
# This resource is AUTHORITATIVE, which mappers.tf avoids for the other
# clients: the built-in list differs between Keycloak versions. It is accepted
# here because this client exists in the local lab only, and its consent
# screen is the point. Built-ins of Keycloak 26.6, minus `roles`.
resource "keycloak_openid_client_default_scopes" "mcp_client" {
  realm_id  = keycloak_realm.docvault.id
  client_id = keycloak_openid_client.mcp_client.id

  default_scopes = ["acr", "basic", "email", "profile", "web-origins"]
}

resource "keycloak_openid_client_optional_scopes" "mcp_client" {
  realm_id  = keycloak_realm.docvault.id
  client_id = keycloak_openid_client.mcp_client.id # the UUID, not the client_id string

  # Authoritative: restates Keycloak's built-in optional scopes this client
  # keeps. Ordinary refresh tokens already let an assistant refresh without a
  # browser; offline_access lets that outlive the user's SSO session.
  optional_scopes = concat(
    [for s in keycloak_openid_client_scope.mcp : s.name],
    ["offline_access"],
  )
}
