# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# ---------------------------------------------------------------------------
# Demo users.
#
# Each one exists to make a specific authorization outcome observable in the
# React app's TokenInspector:
#
#   alice  /acme/engineering  doc.reader + doc.editor   can write Acme docs
#   bob    /globex            doc.reader                cannot see Acme docs at all
#   carol  /acme/legal        doc.reader + doc.admin    needs MFA for classified
#   dave   (no group)         nothing                   authenticated but unauthorized
#                                                       -> proves 403 != 401
# ---------------------------------------------------------------------------
locals {
  users = {
    alice = {
      email      = "alice@acme.test"
      first_name = "Alice"
      last_name  = "Andersson"
      groups     = ["acme_engineering"]
    }
    bob = {
      email      = "bob@globex.test"
      first_name = "Bob"
      last_name  = "Bergstrom"
      groups     = ["globex"]
    }
    carol = {
      email      = "carol@acme.test"
      first_name = "Carol"
      last_name  = "Chen"
      groups     = ["acme_legal"]
    }
    dave = {
      email      = "dave@acme.test"
      first_name = "Dave"
      last_name  = "Diaz"
      groups     = [] # deliberately role-less
    }
  }

  group_ids = {
    acme             = keycloak_group.acme.id
    globex           = keycloak_group.globex.id
    acme_engineering = keycloak_group.acme_engineering.id
    acme_legal       = keycloak_group.acme_legal.id
  }
}

resource "keycloak_user" "demo" {
  for_each = local.users

  realm_id       = keycloak_realm.docvault.id
  username       = each.key
  email          = each.value.email
  first_name     = each.value.first_name
  last_name      = each.value.last_name
  enabled        = true
  email_verified = true

  initial_password {
    value = var.seed_user_password
    # A real deployment sets this true and forces a reset on first login.
    # Left false so the lab and the Playwright suite can log straight in.
    temporary = false
  }
}

# Group membership is managed per USER, and only for the users this file
# creates. `exhaustive = true` means "these are all of this user's groups": a
# membership added by hand shows up in `make plan`, and dave, who has none, is
# checked too.
#
# It used to be managed per group, with keycloak_group_memberships, which is
# authoritative: it owns the group's whole member list. That stopped working the
# moment the realm got a directory (ldap.tf). People from LDAP are members of
# /acme/engineering and /globex too, so every plan wanted to remove them, and
# every apply failed: Keycloak refuses to change a read-only directory's
# memberships. A realm with two sources of people cannot have one of them own
# the list. Owning each local user's own list is still possible, and is what
# this does.
resource "keycloak_user_groups" "demo" {
  for_each = local.users

  realm_id   = keycloak_realm.docvault.id
  user_id    = keycloak_user.demo[each.key].id
  group_ids  = [for g in each.value.groups : local.group_ids[g]]
  exhaustive = true
}

# The old per-group resources are dropped from state without being destroyed.
# Destroying them would take alice, bob and carol out of their groups first.
removed {
  from = keycloak_group_memberships.acme_engineering
  lifecycle {
    destroy = false
  }
}

removed {
  from = keycloak_group_memberships.acme_legal
  lifecycle {
    destroy = false
  }
}

removed {
  from = keycloak_group_memberships.globex
  lifecycle {
    destroy = false
  }
}

# Carol also operates the platform.
resource "keycloak_user_roles" "carol" {
  realm_id = keycloak_realm.docvault.id
  user_id  = keycloak_user.demo["carol"].id
  role_ids = [keycloak_role.platform_admin.id]

  # Not authoritative: leaves her group-derived roles alone.
  exhaustive = false
}
