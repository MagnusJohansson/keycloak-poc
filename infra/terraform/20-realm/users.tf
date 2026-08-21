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

# Group membership is managed per-group (keycloak_group_memberships is
# authoritative for that group's member list).
resource "keycloak_group_memberships" "acme_engineering" {
  realm_id = keycloak_realm.docvault.id
  group_id = keycloak_group.acme_engineering.id
  members  = [for k, v in local.users : keycloak_user.demo[k].username if contains(v.groups, "acme_engineering")]
}

resource "keycloak_group_memberships" "acme_legal" {
  realm_id = keycloak_realm.docvault.id
  group_id = keycloak_group.acme_legal.id
  members  = [for k, v in local.users : keycloak_user.demo[k].username if contains(v.groups, "acme_legal")]
}

resource "keycloak_group_memberships" "globex" {
  realm_id = keycloak_realm.docvault.id
  group_id = keycloak_group.globex.id
  members  = [for k, v in local.users : keycloak_user.demo[k].username if contains(v.groups, "globex")]
}

# Carol also operates the platform.
resource "keycloak_user_roles" "carol" {
  realm_id = keycloak_realm.docvault.id
  user_id  = keycloak_user.demo["carol"].id
  role_ids = [keycloak_role.platform_admin.id]

  # Not authoritative: leaves her group-derived roles alone.
  exhaustive = false
}
