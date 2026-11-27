# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

output "store_id" {
  description = "The OpenFGA store the API uses (Fga__StoreId)."
  value       = openfga_store.docvault.id
}

output "model_id" {
  description = "The authorization model the API is pinned to (Fga__ModelId)."
  value       = openfga_authorization_model.docvault.id
}
