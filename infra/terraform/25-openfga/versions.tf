# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# ---------------------------------------------------------------------------
# 25-openfga : the store and authorization model the API asks "may this person
# see THIS document?" (docs/use-cases/uc1-rbac-document-sharing.md).
#
# A module of its own, not part of 20-realm. 20-realm talks only to Keycloak's
# Admin API, which is what lets it apply unchanged to the local lab and to
# Azure; an OpenFGA provider there would make every realm need an OpenFGA.
#
# Terraform owns the store and the model, NOT the tuples. Tuples change at run
# time (every share, every new document), so tuples in Terraform would make
# `make plan` non-empty after the first share. The API writes them.
#
# Apply after 20-realm. Local lab and CI only: nothing on Azure runs OpenFGA.
# ---------------------------------------------------------------------------
terraform {
  required_version = ">= 1.9"
  required_providers {
    openfga = {
      source  = "openfga/openfga"
      version = "~> 0.5"
    }
  }
}

provider "openfga" {
  api_url   = var.openfga_url
  api_token = var.openfga_api_key
}
