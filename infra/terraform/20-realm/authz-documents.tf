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
# The API asks Keycloak for a decision using the UMA ticket flow rather than
# hardcoding `if (doc.OwnerId == userId)`.
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
# resources are created at runtime by the API when a document is uploaded;
# this declares the type they share.
resource "keycloak_openid_client_authorization_resource" "document" {
  realm_id           = keycloak_realm.docvault.id
  resource_server_id = keycloak_openid_client.api.resource_server_id
  name               = "document"
  type               = "urn:docvault:resources:document"
  display_name       = "DocVault Document"

  uris = ["/documents/*"]

  scopes = [for s in keycloak_openid_client_authorization_scope.document : s.name]

  # Let the resource owner (the uploader) manage access to their own document.
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
resource "keycloak_openid_client_authorization_permission" "document_view" {
  realm_id           = keycloak_realm.docvault.id
  resource_server_id = keycloak_openid_client.api.resource_server_id
  name               = "document-view-permission"
  decision_strategy  = "AFFIRMATIVE"

  resources = [keycloak_openid_client_authorization_resource.document.id]
  scopes    = [keycloak_openid_client_authorization_scope.document["document:view"].id]
  policies  = [keycloak_openid_client_role_policy.reader.id]
}

resource "keycloak_openid_client_authorization_permission" "document_edit" {
  realm_id           = keycloak_realm.docvault.id
  resource_server_id = keycloak_openid_client.api.resource_server_id
  name               = "document-edit-permission"
  decision_strategy  = "AFFIRMATIVE"

  resources = [keycloak_openid_client_authorization_resource.document.id]
  scopes = [
    keycloak_openid_client_authorization_scope.document["document:edit"].id,
    keycloak_openid_client_authorization_scope.document["document:share"].id,
  ]
  policies = [keycloak_openid_client_role_policy.editor.id]
}

# Deleting is admin-only, and UNANIMOUS: every attached policy must pass.
resource "keycloak_openid_client_authorization_permission" "document_delete" {
  realm_id           = keycloak_realm.docvault.id
  resource_server_id = keycloak_openid_client.api.resource_server_id
  name               = "document-delete-permission"
  decision_strategy  = "UNANIMOUS"

  resources = [keycloak_openid_client_authorization_resource.document.id]
  scopes    = [keycloak_openid_client_authorization_scope.document["document:delete"].id]
  policies  = [keycloak_openid_client_role_policy.admin.id]
}

resource "keycloak_openid_client_authorization_permission" "classified_view" {
  realm_id           = keycloak_realm.docvault.id
  resource_server_id = keycloak_openid_client.api.resource_server_id
  name               = "classified-view-permission"
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
