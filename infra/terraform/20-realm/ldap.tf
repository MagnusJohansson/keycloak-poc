# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# ---------------------------------------------------------------------------
# LDAP user federation: people come from a directory, not from this realm.
# The directory says WHO someone is and WHERE they belong. This realm still
# says what belonging there ALLOWS (roles-groups.tf). See
# docs/use-cases/uc7-ldap-directory.md.
#
# Optional: with ldap_connection_url unset, none of this exists. The local lab
# sets it; the Azure deployment has no directory and does not.
# ---------------------------------------------------------------------------
locals {
  ldap = var.ldap_connection_url != null
}

resource "keycloak_ldap_user_federation" "directory" {
  count = local.ldap ? 1 : 0

  realm_id = keycloak_realm.docvault.id
  name     = "directory"
  enabled  = true

  connection_url  = var.ldap_connection_url
  bind_dn         = var.ldap_bind_dn
  bind_credential = var.ldap_bind_credential

  users_dn                = "ou=people,${var.ldap_base_dn}"
  search_scope            = "ONE_LEVEL"
  user_object_classes     = ["inetOrgPerson"]
  username_ldap_attribute = "uid"
  rdn_ldap_attribute      = "uid"
  uuid_ldap_attribute     = "entryUUID"
  vendor                  = "OTHER"

  # Keycloak reads the directory and never writes to it. A password is checked
  # by binding as the user; Keycloak does not store it.
  edit_mode          = "READ_ONLY"
  import_enabled     = true
  sync_registrations = false

  # The directory is the authority on a person's email address.
  trust_email = true

  # Keycloak's default mappers take the first name from `cn`, which in a
  # directory is the full name. The four below replace them.
  delete_default_mappers = true

  connection_timeout = "5s"
  read_timeout       = "10s"

  cache {
    policy = "NO_CACHE"
  }
}

locals {
  # The directory groups this realm accepts membership from, by the realm group
  # they sit under. A directory group is matched by NAME, so each name is looked
  # for in one place only: `engineering` means /acme/engineering and nothing
  # else. Kept by hand: a name listed here with no realm group of that name in
  # that place would be created by Keycloak at a person's first sign-in.
  ldap_groups = {
    top  = { under = "/", names = ["acme", "globex"] }
    acme = { under = "/acme", names = ["engineering"] }
  }

  ldap_attributes = {
    username  = "uid"
    email     = "mail"
    firstName = "givenName"
    lastName  = "sn"
  }
}

resource "keycloak_ldap_user_attribute_mapper" "directory" {
  for_each = local.ldap ? local.ldap_attributes : {}

  realm_id                = keycloak_realm.docvault.id
  ldap_user_federation_id = keycloak_ldap_user_federation.directory[0].id
  name                    = each.key

  user_model_attribute = each.key
  ldap_attribute       = each.value
  read_only            = true

  # A username and an email address are not re-read at each sign-in. Re-read, a
  # person who later took a local user's email address in the directory carried
  # it in her token, marked verified. They still change: at a sync, or when she
  # signs in under a new uid, and Keycloak checks for a duplicate then. Names
  # are re-read: they identify nobody.
  always_read_value_from_ldap = !contains(["username", "email"], each.key)
}

# A person's directory groups are read each time, never copied. The groups
# themselves are this realm's, made in roles-groups.tf.
#
# One mapper per place in the realm's tree, each with its own filter. A single
# mapper over the whole tree (preserve_group_inheritance = true) also worked,
# and matched a name ANYWHERE: a second realm group called `engineering`
# captured Acme's engineers, and a manual group sync rebuilt the tree from the
# directory's nesting. Here the directory's nesting is not used at all.
resource "keycloak_ldap_group_mapper" "directory" {
  for_each = local.ldap ? local.ldap_groups : {}

  realm_id                = keycloak_realm.docvault.id
  ldap_user_federation_id = keycloak_ldap_user_federation.directory[0].id
  name                    = "groups-${each.key}"

  ldap_groups_dn                 = "ou=groups,${var.ldap_base_dn}"
  group_object_classes           = ["groupOfNames"]
  group_name_ldap_attribute      = "cn"
  membership_ldap_attribute      = "member"
  membership_attribute_type      = "DN"
  membership_user_ldap_attribute = "uid"

  # Only these directory groups count. Without a filter, a directory group this
  # realm has never heard of is CREATED here at the person's next sign-in,
  # outside Terraform.
  groups_ldap_filter = "(|${join("", [for g in each.value.names : "(cn=${g})"])})"
  groups_path        = each.value.under

  mode                         = "READ_ONLY"
  user_roles_retrieve_strategy = "LOAD_GROUPS_BY_MEMBER_ATTRIBUTE"
  preserve_group_inheritance   = false

  # Never delete one of this realm's groups because the directory lacks it.
  drop_non_existing_groups_during_sync = false

  # groups_path must exist before Keycloak accepts the mapper.
  depends_on = [keycloak_group.acme, keycloak_group.globex, keycloak_group.acme_engineering]
}
