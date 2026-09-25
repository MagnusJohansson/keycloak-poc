# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# ---------------------------------------------------------------------------
# Step-up authentication (use-case 3).
#
# The goal: reading an ordinary document needs only a password. Opening a
# CLASSIFIED document additionally requires a fresh OTP - even if the user is
# already logged in.
#
# How the pieces connect:
#
#   1. API returns 401 + `WWW-Authenticate: Bearer error="insufficient_user_
#      authentication", acr_values="silver"` when the token's acr is too low.
#   2. The SPA re-runs the auth request with `acr_values=silver`.
#   3. Keycloak maps silver -> LoA 2 via `acr.loa.map` (see realm.tf).
#   4. The flow below sees a requested LoA of 2 and demands OTP.
#   5. The new token carries `acr: "silver"` and the API lets the request through.
#
# NOTE ON ORDERING: keycloak_authentication_execution resources are created in
# API-call order, and Keycloak assigns priority by creation sequence. Terraform
# parallelises by default, so explicit depends_on chains are REQUIRED here.
# Without them the flow comes out shuffled and authentication breaks in ways
# that are painful to debug.
# ---------------------------------------------------------------------------

resource "keycloak_authentication_flow" "browser_stepup" {
  realm_id    = keycloak_realm.docvault.id
  alias       = "browser-stepup"
  description = "Browser flow with level-of-authentication aware step-up."
}

# --- 1. Already have a valid SSO cookie? Then we are done (at LoA 1). -------
resource "keycloak_authentication_execution" "cookie" {
  realm_id          = keycloak_realm.docvault.id
  parent_flow_alias = keycloak_authentication_flow.browser_stepup.alias
  authenticator     = "auth-cookie"
  requirement       = "ALTERNATIVE"
}

# --- 2. Otherwise, run the forms subflow. ----------------------------------
resource "keycloak_authentication_subflow" "forms" {
  realm_id          = keycloak_realm.docvault.id
  parent_flow_alias = keycloak_authentication_flow.browser_stepup.alias
  alias             = "stepup-forms"
  requirement       = "ALTERNATIVE"

  depends_on = [keycloak_authentication_execution.cookie]
}

# --- 2a. LoA 1: username + password ----------------------------------------
resource "keycloak_authentication_subflow" "loa1" {
  realm_id          = keycloak_realm.docvault.id
  parent_flow_alias = keycloak_authentication_subflow.forms.alias
  alias             = "stepup-loa1-password"
  requirement       = "CONDITIONAL"
}

resource "keycloak_authentication_execution" "loa1_condition" {
  realm_id          = keycloak_realm.docvault.id
  parent_flow_alias = keycloak_authentication_subflow.loa1.alias
  authenticator     = "conditional-level-of-authentication"
  requirement       = "REQUIRED"
}

resource "keycloak_authentication_execution_config" "loa1_condition" {
  realm_id     = keycloak_realm.docvault.id
  execution_id = keycloak_authentication_execution.loa1_condition.id
  alias        = "stepup-loa1-condition"

  config = {
    # Run this subflow when the requested LoA is 1 or higher, i.e. always.
    loa-condition-level = "1"
    # Once satisfied, remember it for the whole SSO session.
    loa-max-age = "36000"
  }
}

resource "keycloak_authentication_execution" "password" {
  realm_id          = keycloak_realm.docvault.id
  parent_flow_alias = keycloak_authentication_subflow.loa1.alias
  authenticator     = "auth-username-password-form"
  requirement       = "REQUIRED"

  depends_on = [keycloak_authentication_execution.loa1_condition]
}

# --- 2b. LoA 2: OTP, only when the client actually asked for it ------------
resource "keycloak_authentication_subflow" "loa2" {
  realm_id          = keycloak_realm.docvault.id
  parent_flow_alias = keycloak_authentication_subflow.forms.alias
  alias             = "stepup-loa2-otp"
  requirement       = "CONDITIONAL"

  depends_on = [keycloak_authentication_subflow.loa1]
}

resource "keycloak_authentication_execution" "loa2_condition" {
  realm_id          = keycloak_realm.docvault.id
  parent_flow_alias = keycloak_authentication_subflow.loa2.alias
  authenticator     = "conditional-level-of-authentication"
  requirement       = "REQUIRED"
}

resource "keycloak_authentication_execution_config" "loa2_condition" {
  realm_id     = keycloak_realm.docvault.id
  execution_id = keycloak_authentication_execution.loa2_condition.id
  alias        = "stepup-loa2-condition"

  config = {
    loa-condition-level = "2"
    # Deliberately SHORT. A step-up is meant to be a fresh assertion of
    # presence; making it last all session defeats the purpose.
    loa-max-age = "300"
  }
}

resource "keycloak_authentication_execution" "otp" {
  realm_id          = keycloak_realm.docvault.id
  parent_flow_alias = keycloak_authentication_subflow.loa2.alias
  authenticator     = "auth-otp-form"
  requirement       = "REQUIRED"

  depends_on = [keycloak_authentication_execution.loa2_condition]
}
