# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

resource "openfga_store" "docvault" {
  name = var.store_name
}

# The model is written in OpenFGA's DSL, which is what `make test-model` tests.
# This converts it to the JSON the API takes, the same way for any formatting
# of the source, so a reformatted file is not a change.
data "openfga_authorization_model_document" "docvault" {
  dsl = file("${path.module}/../../openfga/docvault.fga")
}

# OpenFGA models are immutable: a change to the DSL creates a NEW model with a
# new id. The plan says the old one "must be replaced" and the apply reports
# "Destruction complete", but OpenFGA has no way to delete a model: the old id
# still answers (measured with provider 0.5.2 on OpenFGA v1.22.1). Only
# Terraform forgets it. A comment-only change to the DSL is no change at all.
#
# The API is pinned to one id (`make api` reads the output below), so a running
# API keeps deciding by the model it started with until it is restarted. That is
# deliberate: a model write must not change a running service's decisions under
# it.
resource "openfga_authorization_model" "docvault" {
  store_id   = openfga_store.docvault.id
  model_json = data.openfga_authorization_model_document.docvault.result
}
