# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# ---------------------------------------------------------------------------
# The realm itself: token lifetimes, password/OTP policy, and the ACR->LoA map
# that makes step-up authentication (use-case 3) possible.
# ---------------------------------------------------------------------------
resource "keycloak_realm" "docvault" {
  realm        = var.realm_name
  display_name = "DocVault"
  enabled      = true

  login_with_email_allowed = true
  registration_allowed     = false
  reset_password_allowed   = true
  verify_email             = false # keep the lab frictionless; flip on to demo Mailpit

  # Short access tokens + longer SSO session is the standard OIDC posture:
  # a leaked access token expires fast, while the user is not forced to
  # re-authenticate constantly.
  access_token_lifespan        = "5m"
  sso_session_idle_timeout     = "30m"
  sso_session_max_lifespan     = "10h"
  offline_session_idle_timeout = "720h"
  refresh_token_max_reuse      = 0 # reuse detection on: a replayed refresh token kills the session

  ssl_required = "external" # 'external' allows plain HTTP on localhost only

  password_policy = "length(12) and notUsername and passwordHistory(3)"

  otp_policy {
    type              = "totp"
    algorithm         = "HmacSHA1" # what Google Authenticator / 1Password expect
    digits            = 6
    period            = 30
    initial_counter   = 0
    look_ahead_window = 1
  }

  security_defenses {
    brute_force_detection {
      permanent_lockout                = false
      max_login_failures               = 10
      wait_increment_seconds           = 60
      quick_login_check_milli_seconds  = 1000
      minimum_quick_login_wait_seconds = 60
      max_failure_wait_seconds         = 900
      failure_reset_time_seconds       = 43200
    }
    headers {
      x_frame_options                     = "DENY"
      content_security_policy             = "frame-src 'self'; frame-ancestors 'self'; object-src 'none';"
      content_security_policy_report_only = ""
      x_content_type_options              = "nosniff"
      x_robots_tag                        = "none"
      x_xss_protection                    = "1; mode=block"
      strict_transport_security           = "max-age=31536000; includeSubDomains"
    }
  }

  smtp_server {
    host     = var.smtp_host
    port     = var.smtp_port
    from     = "no-reply@docvault.test"
    ssl      = false
    starttls = false
  }

  # -------------------------------------------------------------------------
  # ACR -> Level of Authentication mapping.
  #
  # This is the contract between the authentication flow and the API. The .NET
  # StepUpAcrRequirement asks for acr="silver"; Keycloak resolves that to LoA 2,
  # which the browser flow implements as "password AND OTP".
  #
  #   bronze (1) = password only
  #   silver (2) = password + OTP
  #
  # Without this map, `acr_values=silver` is silently ignored and the step-up
  # demo appears to work while proving nothing.
  # -------------------------------------------------------------------------
  attributes = {
    "acr.loa.map" = jsonencode({
      bronze = 1
      silver = 2
    })
  }
}

# Which of the flows defined in authn-stepup.tf the realm actually uses.
resource "keycloak_authentication_bindings" "docvault" {
  realm_id     = keycloak_realm.docvault.id
  browser_flow = keycloak_authentication_flow.browser_stepup.alias
}
