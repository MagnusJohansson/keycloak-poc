# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# ---------------------------------------------------------------------------
# A STAND-IN for a legacy SAML identity provider.
#
# This is not part of DocVault. It plays the system DocVault is moving away
# from: an identity provider that speaks SAML 2.0, has its own people and its
# own group names, and knows nothing of DocVault's roles. It is a second realm
# in the same Keycloak, because that needs no extra container. What it does not
# do is prove interoperability with another vendor's product.
#
# The other side, DocVault trusting it, is saml-broker.tf.
# See docs/use-cases/uc8-saml-legacy-idp.md.
# ---------------------------------------------------------------------------
locals {
  saml = var.saml_legacy_idp_enabled

  # A SAML party is named by its entity ID. Keycloak uses the realm's URL.
  docvault_entity_id = "${var.keycloak_url}/realms/${var.realm_name}"
  saml_broker_alias  = "legacy-saml"
  saml_broker_acs    = "${var.keycloak_url}/realms/${var.realm_name}/broker/legacy-saml/endpoint"

  legacy_people = {
    heidi = { email = "heidi@acme.test", first_name = "Heidi", last_name = "Hansen", groups = ["ACME-ENG"] }
    ivan  = { email = "ivan@globex.test", first_name = "Ivan", last_name = "Ivanov", groups = ["GLOBEX"] }
    judy  = { email = "judy@acme.test", first_name = "Judy", last_name = "Jensen", groups = [] }
  }
}

resource "keycloak_realm" "legacy" {
  count = local.saml ? 1 : 0

  realm        = "legacy-idp"
  enabled      = true
  display_name = "Legacy sign-in (a stand-in)"
  ssl_required = "external"
}

# The legacy system's own group names. They mean nothing to DocVault until
# saml-broker.tf maps them.
resource "keycloak_group" "legacy" {
  for_each = local.saml ? toset(["ACME-ENG", "GLOBEX"]) : toset([])

  realm_id = keycloak_realm.legacy[0].id
  name     = each.key
}

resource "keycloak_user" "legacy" {
  for_each = local.saml ? local.legacy_people : {}

  realm_id       = keycloak_realm.legacy[0].id
  username       = each.key
  email          = each.value.email
  first_name     = each.value.first_name
  last_name      = each.value.last_name
  enabled        = true
  email_verified = true

  initial_password {
    value     = var.seed_user_password
    temporary = false
  }
}

resource "keycloak_user_groups" "legacy" {
  for_each = local.saml ? local.legacy_people : {}

  realm_id   = keycloak_realm.legacy[0].id
  user_id    = keycloak_user.legacy[each.key].id
  group_ids  = [for g in each.value.groups : keycloak_group.legacy[g].id]
  exhaustive = true
}

# Each realm's signing certificate, for the other side to check signatures with.
data "keycloak_realm_keys" "legacy" {
  count = local.saml ? 1 : 0

  realm_id   = keycloak_realm.legacy[0].id
  algorithms = ["RS256"]
  status     = ["ACTIVE"]
}

data "keycloak_realm_keys" "docvault" {
  count = local.saml ? 1 : 0

  realm_id   = keycloak_realm.docvault.id
  algorithms = ["RS256"]
  status     = ["ACTIVE"]
}

# DocVault, as the legacy provider sees it: a SAML service provider.
resource "keycloak_saml_client" "docvault_at_legacy" {
  count = local.saml ? 1 : 0

  realm_id  = keycloak_realm.legacy[0].id
  client_id = local.docvault_entity_id
  name      = "DocVault (Keycloak)"

  # Both the response and the assertion inside it are signed.
  sign_documents          = true
  sign_assertions         = true
  signature_algorithm     = "RSA_SHA256"
  include_authn_statement = true

  # It answers only requests DocVault signed, and only to DocVault's address.
  client_signature_required   = true
  signing_certificate         = data.keycloak_realm_keys.docvault[0].keys[0].certificate
  valid_redirect_uris         = [local.saml_broker_acs]
  assertion_consumer_post_url = local.saml_broker_acs
  force_post_binding          = true

  # An opaque, stable identifier per person. Not a username, not an address.
  name_id_format       = "persistent"
  force_name_id_format = true

  full_scope_allowed = false
}

locals {
  legacy_attributes = { uid = "username", mail = "email", givenName = "firstName", sn = "lastName" }
}

resource "keycloak_saml_user_property_protocol_mapper" "legacy" {
  for_each = local.saml ? local.legacy_attributes : {}

  realm_id                   = keycloak_realm.legacy[0].id
  client_id                  = keycloak_saml_client.docvault_at_legacy[0].id
  name                       = each.key
  user_property              = each.value
  saml_attribute_name        = each.key
  saml_attribute_name_format = "Basic"
}

resource "keycloak_generic_protocol_mapper" "legacy_groups" {
  count = local.saml ? 1 : 0

  realm_id        = keycloak_realm.legacy[0].id
  client_id       = keycloak_saml_client.docvault_at_legacy[0].id
  name            = "memberOf"
  protocol        = "saml"
  protocol_mapper = "saml-group-membership-mapper"
  config = {
    "attribute.name"       = "memberOf"
    "attribute.nameformat" = "Basic"
    "single"               = "true"
    "full.path"            = "false"
  }
}
