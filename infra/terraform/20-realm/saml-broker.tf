# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# ---------------------------------------------------------------------------
# DocVault brokers a legacy SAML identity provider.
#
# People who still live in the old system sign in there. Keycloak checks the
# SAML assertion it gets back, turns it into a DocVault user with DocVault
# groups, and issues ITS OWN token. The API and the clients see one issuer and
# one token format; SAML stops at this file.
#
# The legacy provider here is the stand-in in saml-legacy-idp.tf.
# See docs/use-cases/uc8-saml-legacy-idp.md.
# ---------------------------------------------------------------------------
resource "keycloak_saml_identity_provider" "legacy" {
  count = local.saml ? 1 : 0

  realm        = keycloak_realm.docvault.id
  alias        = local.saml_broker_alias
  display_name = "Legacy sign-in (SAML)"
  enabled      = true

  entity_id                  = local.docvault_entity_id
  single_sign_on_service_url = "${var.keycloak_url}/realms/${keycloak_realm.legacy[0].realm}/protocol/saml"
  single_logout_service_url  = "${var.keycloak_url}/realms/${keycloak_realm.legacy[0].realm}/protocol/saml"

  # What DocVault demands of an answer: signed by the legacy provider's key,
  # the assertion itself signed too.
  signing_certificate    = data.keycloak_realm_keys.legacy[0].keys[0].certificate
  validate_signature     = true
  want_assertions_signed = true

  # What DocVault sends: signed requests, by POST.
  want_authn_requests_signed = true
  signature_algorithm        = "RSA_SHA256"
  post_binding_authn_request = true
  post_binding_response      = true
  post_binding_logout        = true

  # The person is the assertion's subject: an opaque, stable identifier.
  name_id_policy_format = "Persistent"
  principal_type        = "SUBJECT"

  # What is taken from the legacy system once, and what at every sign-in, is
  # decided per mapper below. The provider-wide setting is IMPORT because FORCE
  # here also re-writes the person's email address at every sign-in, whatever
  # the email mapper says, and that ran into the realm's unique-email rule: a
  # brokered person whose address in the legacy system later became a DocVault
  # user's got a bare HTTP 500 at her next sign-in.
  sync_mode = "IMPORT"

  # The legacy provider's word is not enough to tie a person to an existing
  # DocVault account by email address. See the use case.
  trust_email = false

  store_token = false

  first_broker_login_flow_alias = keycloak_authentication_flow.first_broker_no_link[0].alias
}

# What happens the first time DocVault meets a brokered person.
#
# Keycloak's built-in "first broker login" flow, on finding that the person's
# email address already belongs to a DocVault user, offers to LINK the two
# accounts, and emails the existing user a confirmation link. So anyone who
# can set an email address in the legacy system can make DocVault send alice a
# genuine email, and if she clicks it, sign in as her.
#
# This flow has no linking. A brokered person is a new DocVault user or is
# refused. Linking a legacy identity to an existing account is then a job for
# an administrator.
resource "keycloak_authentication_flow" "first_broker_no_link" {
  count = local.saml ? 1 : 0

  realm_id    = keycloak_realm.docvault.id
  alias       = "first-broker-login-no-link"
  description = "First sign-in through an identity provider: create a new user, never link to an existing one."
}

# Executions are ordered by `priority` and nothing else (see authn-stepup.tf).
resource "keycloak_authentication_execution" "first_broker_review" {
  count = local.saml ? 1 : 0

  realm_id          = keycloak_realm.docvault.id
  parent_flow_alias = keycloak_authentication_flow.first_broker_no_link[0].alias
  authenticator     = "idp-review-profile"
  requirement       = "REQUIRED"
  priority          = 10
}

resource "keycloak_authentication_execution_config" "first_broker_review" {
  count = local.saml ? 1 : 0

  realm_id     = keycloak_realm.docvault.id
  execution_id = keycloak_authentication_execution.first_broker_review[0].id
  alias        = "first-broker-review-profile"
  config = {
    # Ask the person only for what the provider did not send.
    "update.profile.on.first.login" = "missing"
  }
}

resource "keycloak_authentication_execution" "first_broker_create" {
  count = local.saml ? 1 : 0

  realm_id          = keycloak_realm.docvault.id
  parent_flow_alias = keycloak_authentication_flow.first_broker_no_link[0].alias
  authenticator     = "idp-create-user-if-unique"
  requirement       = "REQUIRED"
  priority          = 20
}

# A brokered person's username carries the provider's name, so that the legacy
# system's `alice` can never be, or collide with, DocVault's `alice`.
resource "keycloak_user_template_importer_identity_provider_mapper" "legacy_username" {
  count = local.saml ? 1 : 0

  realm                   = keycloak_realm.docvault.id
  identity_provider_alias = keycloak_saml_identity_provider.legacy[0].alias
  name                    = "username"
  template                = "$${ALIAS}.$${ATTRIBUTE.uid}"

  extra_config = {
    syncMode = "IMPORT" # a username is for life
  }
}

locals {
  saml_imported = { email = "mail", firstName = "givenName", lastName = "sn" }

  # The legacy system's group names, and what they mean here.
  saml_groups = {
    "ACME-ENG" = "/acme/engineering"
    "GLOBEX"   = "/globex"
  }
}

resource "keycloak_attribute_importer_identity_provider_mapper" "legacy" {
  for_each = local.saml ? local.saml_imported : {}

  realm                   = keycloak_realm.docvault.id
  identity_provider_alias = keycloak_saml_identity_provider.legacy[0].alias
  name                    = each.key
  attribute_name          = each.value
  user_attribute          = each.key

  extra_config = {
    # The email address is taken at the first sign-in and not again. Names
    # follow the legacy system.
    syncMode = each.key == "email" ? "IMPORT" : "FORCE"
  }
}

resource "keycloak_custom_identity_provider_mapper" "legacy_groups" {
  for_each = local.saml ? local.saml_groups : {}

  realm                    = keycloak_realm.docvault.id
  identity_provider_alias  = keycloak_saml_identity_provider.legacy[0].alias
  name                     = "group ${each.key}"
  identity_provider_mapper = "saml-advanced-group-idp-mapper"

  extra_config = {
    # At EVERY sign-in, so that a person moved to another group in the legacy
    # system moves here too, and one taken out of a group loses it. With
    # IMPORT the mapper runs once and a mover never moves.
    syncMode                     = "FORCE"
    attributes                   = jsonencode([{ key = "memberOf", value = each.key }])
    "are.attribute.values.regex" = "false"
    group                        = each.value
  }

  depends_on = [keycloak_group.acme_engineering, keycloak_group.globex]
}
