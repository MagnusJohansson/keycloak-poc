# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# ---------------------------------------------------------------------------
# The API looking people up, to share a document with them (UC1).
#
# A share names a person by username, and OpenFGA records her Keycloak user id:
# a username can be renamed, or given to the next person, who would inherit the
# grants. Turning one into the other, and checking that the person is in the
# caller's tenant, takes the Admin API.
#
# A client of its own rather than docvault-api's service account. That one is
# the Authorization Services resource server; reading the realm's people is a
# different power, and a separate secret keeps the two apart.
#
# view-users reads every person in the realm, both tenants. The API answers a
# lookup for someone outside the caller's tenant exactly as for nobody, so the
# lookup does not tell alice who is in globex. Narrowing the role itself to
# one tenant would need fine-grained admin permissions; not done here.
# ---------------------------------------------------------------------------
resource "keycloak_openid_client" "api_directory" {
  realm_id  = keycloak_realm.docvault.id
  client_id = "docvault-api-directory"
  name      = "DocVault API: people lookup for sharing"
  enabled   = true

  access_type                  = "CONFIDENTIAL"
  service_accounts_enabled     = true
  standard_flow_enabled        = false
  direct_access_grants_enabled = false
}

data "keycloak_openid_client" "realm_management" {
  realm_id  = keycloak_realm.docvault.id
  client_id = "realm-management"
}

resource "keycloak_openid_client_service_account_role" "api_directory_view_users" {
  realm_id                = keycloak_realm.docvault.id
  service_account_user_id = keycloak_openid_client.api_directory.service_account_user_id
  client_id               = data.keycloak_openid_client.realm_management.id
  role                    = "view-users"
}
