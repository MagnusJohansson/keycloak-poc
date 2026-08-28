# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# ---------------------------------------------------------------------------
# Roles and groups.
#
# The distinction trips people up constantly, so this file is deliberately
# explicit about it:
#
#   ROLE  = what you are allowed to DO          (doc.editor)
#   GROUP = who you BELONG TO                   (/acme/engineering)
#
# Users get groups. Groups carry roles. Users never get roles directly - that
# way "onboard a new Acme engineer" is one group membership, not a checklist.
# ---------------------------------------------------------------------------

# --- Realm role: crosses all clients ---------------------------------------
resource "keycloak_role" "platform_admin" {
  realm_id    = keycloak_realm.docvault.id
  name        = "platform-admin"
  description = "Operate DocVault itself. Deliberately rare."
}

# --- Client roles: scoped to the API, which is what actually enforces them --
locals {
  api_roles = {
    "doc.reader" = "View documents in your own tenant."
    "doc.editor" = "Create and modify documents in your own tenant."
    "doc.admin"  = "Manage documents and sharing for your own tenant."
  }
}

resource "keycloak_role" "api" {
  for_each = local.api_roles

  realm_id    = keycloak_realm.docvault.id
  client_id   = keycloak_openid_client.api.id
  name        = each.key
  description = each.value
}

# --- Tenants as top-level groups -------------------------------------------
# The `tenant` attribute is what gets minted into the token by the mapper in
# mappers.tf, and what the API uses to scope every query.
resource "keycloak_group" "acme" {
  realm_id = keycloak_realm.docvault.id
  name     = "acme"
  attributes = {
    tenant = "acme"
  }
}

resource "keycloak_group" "globex" {
  realm_id = keycloak_realm.docvault.id
  name     = "globex"
  attributes = {
    tenant = "globex"
  }
}

# --- Departments as sub-groups ---------------------------------------------
# Sub-groups inherit their parent's roles, so an Acme engineer automatically
# gets everything an Acme member gets.
resource "keycloak_group" "acme_engineering" {
  realm_id  = keycloak_realm.docvault.id
  parent_id = keycloak_group.acme.id
  name      = "engineering"
  attributes = {
    tenant     = "acme"
    department = "engineering"
  }
}

resource "keycloak_group" "acme_legal" {
  realm_id  = keycloak_realm.docvault.id
  parent_id = keycloak_group.acme.id
  name      = "legal"
  attributes = {
    tenant     = "acme"
    department = "legal"
  }
}

# --- Wire roles onto groups -------------------------------------------------
# Baseline: anyone in a tenant can read.
resource "keycloak_group_roles" "acme" {
  realm_id = keycloak_realm.docvault.id
  group_id = keycloak_group.acme.id
  role_ids = [keycloak_role.api["doc.reader"].id]
}

resource "keycloak_group_roles" "globex" {
  realm_id = keycloak_realm.docvault.id
  group_id = keycloak_group.globex.id
  role_ids = [keycloak_role.api["doc.reader"].id]
}

# Engineering can also write. Note this does NOT restate doc.reader - the
# sub-group inherits it from /acme.
resource "keycloak_group_roles" "acme_engineering" {
  realm_id = keycloak_realm.docvault.id
  group_id = keycloak_group.acme_engineering.id
  role_ids = [keycloak_role.api["doc.editor"].id]
}

# Legal gets read-only plus the ability to see classified material - but only
# after stepping up to MFA. The role alone is not sufficient; see authn-stepup.tf.
resource "keycloak_group_roles" "acme_legal" {
  realm_id = keycloak_realm.docvault.id
  group_id = keycloak_group.acme_legal.id
  role_ids = [keycloak_role.api["doc.admin"].id]
}
