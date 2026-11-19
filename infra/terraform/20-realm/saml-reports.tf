# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# ---------------------------------------------------------------------------
# A legacy application that only speaks SAML, signing in through Keycloak.
#
# The other direction from saml-broker.tf. There, Keycloak is the service
# provider and an old system is the identity provider. Here Keycloak IS the
# identity provider, and the old thing is an application: apps/saml-reports.
# Its people are whoever this realm can sign in: local users, the directory's,
# and the legacy provider's.
#
# See docs/use-cases/uc9-saml-application.md.
# ---------------------------------------------------------------------------
locals {
  reports_entity_id = "${var.reports_url}/saml"
  reports_acs       = "${var.reports_url}/saml/acs"
}

# The application's own key pair, for signing its sign-in requests. Made here
# so that Keycloak gets the certificate and the application gets the key from
# one place. The key is in Terraform's state, like the client secrets.
resource "tls_private_key" "reports" {
  algorithm = "RSA"
  rsa_bits  = 2048
}

resource "tls_self_signed_cert" "reports" {
  private_key_pem = tls_private_key.reports.private_key_pem

  subject {
    common_name = "docvault-reports"
  }

  # Ten years, and never renewed by a later apply: a renewal would change the
  # certificate under a running application. Rotating it is a deliberate act
  # (taint the key), not a side effect.
  validity_period_hours = 87600
  early_renewal_hours   = 0
  allowed_uses          = ["digital_signature"]
}

resource "keycloak_saml_client" "reports" {
  realm_id  = keycloak_realm.docvault.id
  client_id = local.reports_entity_id
  name      = "DocVault Reports (legacy, SAML)"

  # The response and the assertion inside it are both signed.
  sign_documents          = true
  sign_assertions         = true
  signature_algorithm     = "RSA_SHA256"
  include_authn_statement = true

  # Keycloak answers only requests the application signed, and only to its
  # one address. Keycloak takes the certificate without the PEM armour.
  client_signature_required = true
  signing_certificate = replace(replace(replace(
    tls_self_signed_cert.reports.cert_pem, "-----BEGIN CERTIFICATE-----", ""),
  "-----END CERTIFICATE-----", ""), "\n", "")
  valid_redirect_uris         = [local.reports_acs]
  assertion_consumer_post_url = local.reports_acs
  force_post_binding          = true

  # The subject is the username. A persistent, opaque identifier would be
  # better, and it is what saml-legacy-idp.tf uses, but Keycloak keeps a
  # persistent NameID as an attribute on the user, and it cannot write one to a
  # person from the read-only LDAP directory. Her sign-in then fails, and
  # Keycloak reports it as a wrong password. See the use case.
  name_id_format       = "username"
  force_name_id_format = true

  # No roles in the assertion: the application gets what the mappers below
  # send, and nothing else.
  full_scope_allowed = false
}

locals {
  reports_attributes = { username = "username", email = "email" }
}

resource "keycloak_saml_user_property_protocol_mapper" "reports" {
  for_each = local.reports_attributes

  realm_id                   = keycloak_realm.docvault.id
  client_id                  = keycloak_saml_client.reports.id
  name                       = each.key
  user_property              = each.value
  saml_attribute_name        = each.key
  saml_attribute_name_format = "Basic"
}

# Groups by full path, as the API gets them: the application takes the tenant
# from the first segment.
resource "keycloak_generic_protocol_mapper" "reports_groups" {
  realm_id        = keycloak_realm.docvault.id
  client_id       = keycloak_saml_client.reports.id
  name            = "groups"
  protocol        = "saml"
  protocol_mapper = "saml-group-membership-mapper"
  config = {
    "attribute.name"       = "groups"
    "attribute.nameformat" = "Basic"
    "single"               = "true"
    "full.path"            = "true"
  }
}

# The realm's signing certificate, for the application to pin.
data "keycloak_realm_keys" "signing" {
  realm_id   = keycloak_realm.docvault.id
  algorithms = ["RS256"]
  status     = ["ACTIVE"]
}
