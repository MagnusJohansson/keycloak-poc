# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

variable "openfga_url" {
  description = "OpenFGA's HTTP API, as this machine reaches it."
  type        = string
}

variable "openfga_api_key" {
  description = "A preshared key OpenFGA accepts (OPENFGA_AUTHN_PRESHARED_KEYS)."
  type        = string
  sensitive   = true
}

variable "store_name" {
  description = "Name of the OpenFGA store. A name is not unique in OpenFGA; the store's id is what the API is given."
  type        = string
  default     = "docvault"
}
