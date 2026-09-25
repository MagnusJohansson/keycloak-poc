# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# ---------------------------------------------------------------------------
# Fine-grained authorization (use-case 1).
#
# RBAC answers "may this user edit documents?". It cannot answer "may this user
# edit THIS document?" - that depends on data, not on the token. Keycloak's
# Authorization Services move that second decision into Keycloak, where it can
# be audited and changed without redeploying the API.
#
# The model:
#   RESOURCE   a document (or the whole collection)
#   SCOPE      an action: view / edit / share / delete
#   POLICY     a rule: "is a doc.editor", "is the owner"
#   PERMISSION binds scopes on a resource to policies
#
# STATUS: this model is provisioned but NOT enforced at runtime. The API does
# local RBAC on the roles in the token plus tenant scoping from the group path;
# it never asks Keycloak for a decision. Wiring that up means the UMA ticket
# flow (`grant_type=urn:ietf:params:oauth:grant-type:uma-ticket`): the API
# forwards the user's access token as the bearer and gets back an RPT, or a
# yes/no with `response_mode=decision`. That needs only the user's token, but
# an outbound call per decision and Keycloak on the request path. Deciding per
# DOCUMENT also means registering each one through the Protection API, which
# accepts only the resource server's own token (so the client secret) and
# needs allow_remote_resource_management, off here. That trade is why it is
# modelled here and evaluated in C# - see DocumentEndpoints.cs.
# ---------------------------------------------------------------------------

locals {
  document_scopes = ["document:view", "document:edit", "document:share", "document:delete"]
}

resource "keycloak_openid_client_authorization_scope" "document" {
  for_each = toset(local.document_scopes)

  realm_id           = keycloak_realm.docvault.id
  resource_server_id = keycloak_openid_client.api.resource_server_id
  name               = each.value
}

# A single resource TYPE covering every document instance. Per-instance
# resources would be registered through the Protection API as documents are
# uploaded; this declares the type they would share. The API does not register
# them today - see the STATUS note above.
resource "keycloak_openid_client_authorization_resource" "document" {
  realm_id           = keycloak_realm.docvault.id
  resource_server_id = keycloak_openid_client.api.resource_server_id
  name               = "document"
  type               = "urn:docvault:resources:document"
  display_name       = "DocVault Document"

  uris = ["/documents/*"]

  scopes = [for s in keycloak_openid_client_authorization_scope.document : s.name]

  # Lets a resource's OWNER grant access to other users (UMA sharing). On this
  # type-level resource the owner is the resource server itself, not a user, so
  # nothing can be shared yet. Sharing needs each document registered through the
  # Protection API with its uploader as owner, which the API does not do (STATUS
  # above). The flag records the intent; on its own it enables nothing.
  owner_managed_access = true
}

# Classified documents are a separate type so they can carry a stricter
# permission without affecting ordinary ones.
resource "keycloak_openid_client_authorization_resource" "classified_document" {
  realm_id           = keycloak_realm.docvault.id
  resource_server_id = keycloak_openid_client.api.resource_server_id
  name               = "classified-document"
  type               = "urn:docvault:resources:classified"
  display_name       = "Classified Document"

  uris = ["/documents/classified/*"]

  scopes = [for s in keycloak_openid_client_authorization_scope.document : s.name]
}

# --- Policies ---------------------------------------------------------------
resource "keycloak_openid_client_role_policy" "editor" {
  realm_id           = keycloak_realm.docvault.id
  resource_server_id = keycloak_openid_client.api.resource_server_id
  name               = "is-document-editor"
  type               = "role"
  decision_strategy  = "AFFIRMATIVE"
  logic              = "POSITIVE"

  role {
    id       = keycloak_role.api["doc.editor"].id
    required = false
  }
  role {
    id       = keycloak_role.api["doc.admin"].id
    required = false
  }
}

resource "keycloak_openid_client_role_policy" "reader" {
  realm_id           = keycloak_realm.docvault.id
  resource_server_id = keycloak_openid_client.api.resource_server_id
  name               = "is-document-reader"
  type               = "role"
  decision_strategy  = "AFFIRMATIVE"
  logic              = "POSITIVE"

  role {
    id       = keycloak_role.api["doc.reader"].id
    required = false
  }
  role {
    id       = keycloak_role.api["doc.editor"].id
    required = false
  }
  role {
    id       = keycloak_role.api["doc.admin"].id
    required = false
  }
}

resource "keycloak_openid_client_role_policy" "admin" {
  realm_id           = keycloak_realm.docvault.id
  resource_server_id = keycloak_openid_client.api.resource_server_id
  name               = "is-document-admin"
  type               = "role"
  decision_strategy  = "AFFIRMATIVE"
  logic              = "POSITIVE"

  role {
    id       = keycloak_role.api["doc.admin"].id
    required = false
  }
}

# --- Permissions ------------------------------------------------------------
# Every permission is `type = "scope"`. Leave it unset and the provider creates a
# RESOURCE permission, which ignores `scopes` and applies to every scope of the
# resource. With the resource server's UNANIMOUS strategy, a request for
# document:view then also had to pass document-delete-permission, so only an
# admin could view anything. Nothing reported it: the model is not called at
# runtime (see STATUS above). Check with the admin console's policy evaluator,
# or a UMA `response_mode=decision` call with a user's token.
#
# UPGRADE PATH. Keycloak ignores a `type` change on an existing permission: the
# apply reports success, Keycloak keeps `resource`, and the next plan repeats
# the change forever. The provider does not mark `type` force-new, and
# `replace_triggered_by` on a new terraform_data does not fire either (creating
# the trigger is not a change to it - tried). So the fix gives the permissions
# NEW resource addresses (`*_scope`) and NEW names (`*-scope-permission`):
# `make seed` on a realm seeded before the fix destroys the old resource
# permissions and creates scope ones. New names matter too: old and new run in
# parallel, and reusing a name could collide with the permission being deleted.
resource "keycloak_openid_client_authorization_permission" "document_view_scope" {
  realm_id           = keycloak_realm.docvault.id
  resource_server_id = keycloak_openid_client.api.resource_server_id
  name               = "document-view-scope-permission"
  type               = "scope"
  decision_strategy  = "AFFIRMATIVE"

  resources = [keycloak_openid_client_authorization_resource.document.id]
  scopes    = [keycloak_openid_client_authorization_scope.document["document:view"].id]
  policies  = [keycloak_openid_client_role_policy.reader.id]
}

resource "keycloak_openid_client_authorization_permission" "document_edit_scope" {
  realm_id           = keycloak_realm.docvault.id
  resource_server_id = keycloak_openid_client.api.resource_server_id
  name               = "document-edit-scope-permission"
  type               = "scope"
  decision_strategy  = "AFFIRMATIVE"

  resources = [keycloak_openid_client_authorization_resource.document.id]
  scopes = [
    keycloak_openid_client_authorization_scope.document["document:edit"].id,
    keycloak_openid_client_authorization_scope.document["document:share"].id,
  ]
  policies = [keycloak_openid_client_role_policy.editor.id]
}

# Deleting is admin-only, and UNANIMOUS: every attached policy must pass.
resource "keycloak_openid_client_authorization_permission" "document_delete_scope" {
  realm_id           = keycloak_realm.docvault.id
  resource_server_id = keycloak_openid_client.api.resource_server_id
  name               = "document-delete-scope-permission"
  type               = "scope"
  decision_strategy  = "UNANIMOUS"

  resources = [keycloak_openid_client_authorization_resource.document.id]
  scopes    = [keycloak_openid_client_authorization_scope.document["document:delete"].id]
  policies  = [keycloak_openid_client_role_policy.admin.id]
}

resource "keycloak_openid_client_authorization_permission" "classified_view_scope" {
  realm_id           = keycloak_realm.docvault.id
  resource_server_id = keycloak_openid_client.api.resource_server_id
  name               = "classified-view-scope-permission"
  type               = "scope"
  decision_strategy  = "UNANIMOUS"

  resources = [keycloak_openid_client_authorization_resource.classified_document.id]
  scopes    = [keycloak_openid_client_authorization_scope.document["document:view"].id]
  policies  = [keycloak_openid_client_role_policy.admin.id]
}

# --- The worker's service account -------------------------------------------
# Use-case 4. The daemon gets doc.reader so it can run nightly indexing, and
# nothing more. It cannot edit or delete - least privilege for a machine
# identity exactly as for a human one.
resource "keycloak_openid_client_service_account_role" "worker_reader" {
  realm_id                = keycloak_realm.docvault.id
  service_account_user_id = keycloak_openid_client.worker.service_account_user_id
  client_id               = keycloak_openid_client.api.id
  role                    = keycloak_role.api["doc.reader"].name
}
