# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# Local OpenFGA (`make up`). The key is the lab default in
# infra/local/docker-compose.yml, against a throwaway container, like admin/admin
# in realm.tfvars; committed because `make seed` and CI depend on it.
openfga_url     = "http://localhost:8090"
openfga_api_key = "docvault-fga-lab-key"
