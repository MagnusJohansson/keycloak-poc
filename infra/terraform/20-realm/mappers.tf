# ---------------------------------------------------------------------------
# Protocol mappers - what actually ends up inside the token.
#
# Mappers are attached per-client rather than via a shared client scope on
# purpose. `keycloak_openid_client_default_scopes` is AUTHORITATIVE: it replaces
# the entire default-scope list, so you must restate every built-in
# (acr, basic, email, profile, roles, web-origins, and `organization` when that
# feature is on). That list differs between Keycloak versions and between
# deployments, which would break the "same HCL everywhere" property this repo is
# built on. Per-client mappers are slightly more verbose and completely portable.
# ---------------------------------------------------------------------------

locals {
  # Every client that obtains a token intended for the API.
  api_calling_clients = merge(
    { for k, v in keycloak_openid_client.spa : k => v.id },
    { for k, v in keycloak_openid_client.mobile : k => v.id },
    {
      "docvault-desktop" = keycloak_openid_client.desktop.id
      "docvault-winui"   = keycloak_openid_client.winui.id
      "docvault-worker"  = keycloak_openid_client.worker.id
    }
  )
}

# --- Audience -------------------------------------------------------------
# THE most common Keycloak + .NET failure. Without this the access token's
# `aud` is the *calling* client, the API's `ValidateAudience` rejects it, and
# you get an opaque 401 with nothing useful in the logs.
#
# Symptom: 401 with `WWW-Authenticate: Bearer error="invalid_token",
#          error_description="The audience 'docvault-web-react' is invalid"`
resource "keycloak_openid_audience_protocol_mapper" "api_audience" {
  for_each = local.api_calling_clients

  realm_id  = keycloak_realm.docvault.id
  client_id = each.value
  name      = "docvault-api-audience"

  included_client_audience = keycloak_openid_client.api.client_id
  add_to_access_token      = true
  add_to_id_token          = false # the ID token is about the user, not the API
}

# --- Group membership -----------------------------------------------------
# Emits e.g. ["/acme/engineering"]. The API derives tenant + department from
# this path (see TenantClaimExtensions.cs).
#
# Note: the `tenant` / `department` attributes set on the groups in
# roles-groups.tf are admin-side metadata. Keycloak has NO built-in mapper that
# copies group attributes into a token - the path is the thing that travels.
# That is why the API parses the path rather than reading a `tenant` claim.
resource "keycloak_openid_group_membership_protocol_mapper" "groups" {
  for_each = local.api_calling_clients

  realm_id  = keycloak_realm.docvault.id
  client_id = each.value
  name      = "groups"

  claim_name          = "groups"
  full_path           = true
  add_to_id_token     = true
  add_to_access_token = true
  add_to_userinfo     = true
}

# --- Analytics: pairwise subject identifier -------------------------------
# Use-case 5. Replaces the stable `sub` UUID with a hash that is unique to this
# client. The analytics service can still tell two sessions apart, but cannot
# correlate a user against any other client's records - including the main app.
# This is a real privacy control, not a cosmetic one.
resource "keycloak_generic_protocol_mapper" "analytics_pairwise_sub" {
  realm_id        = keycloak_realm.docvault.id
  client_id       = keycloak_openid_client.analytics.id
  name            = "pairwise-sub"
  protocol        = "openid-connect"
  protocol_mapper = "oidc-sha256-pairwise-sub-mapper"

  # Deliberately empty; both plausible settings are wrong here.
  #
  # 1. No `sectorIdentifierUri`. Keycloak FETCHES that URL over HTTP while
  #    creating the mapper and refuses if it is unreachable ("Failed to get
  #    redirect URIs from the Sector Identifier URI"), which would make this
  #    module depend on the API already running - a chicken-and-egg problem,
  #    since the API needs the realm. A sector identifier is only required when a
  #    client's redirect URIs span multiple hosts; this one has a single host, so
  #    Keycloak derives the sector from it. If you later add hosts, publish the
  #    document (the API exposes /analytics/sector-identifier) and set it here.
  #
  # 2. No hardcoded salt. Keycloak generates `pairwiseSubAlgorithmSalt` itself.
  #    Setting it here would both be ignored AND commit a secret to git - anyone
  #    holding the salt and the subject list can undo the pseudonymisation.
  config = {}

  lifecycle {
    # The server-generated salt is not in the configuration, so without this every
    # subsequent plan proposes deleting it: a perpetual diff that trains people to
    # ignore `terraform plan` output.
    ignore_changes = [config["pairwiseSubAlgorithmSalt"]]
  }
}

# An OPTIONAL scope: only granted if the client explicitly asks for it AND the
# user consents. Contrast with a default scope, which is silently always on.
resource "keycloak_openid_client_scope" "analytics_read" {
  realm_id               = keycloak_realm.docvault.id
  name                   = "analytics:read"
  description            = "Read aggregated, de-identified usage statistics."
  include_in_token_scope = true
  consent_screen_text    = "View anonymised usage statistics"
}

resource "keycloak_openid_client_optional_scopes" "analytics" {
  realm_id = keycloak_realm.docvault.id

  # The client's internal UUID (.id), NOT its OAuth client_id string. Passing the
  # latter fails with "client with id docvault-analytics does not exist", which
  # reads like a race condition but is simply the wrong identifier.
  client_id = keycloak_openid_client.analytics.id

  optional_scopes = [keycloak_openid_client_scope.analytics_read.name]
}
