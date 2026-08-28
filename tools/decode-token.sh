#!/usr/bin/env bash
# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# Mints a client_credentials token for the worker and pretty-prints its claims.
#
# The fastest way to answer "why is the API rejecting me?" - check `aud`,
# `resource_access` and `exp` before touching any application code.
set -euo pipefail

KEYCLOAK_URL="${KEYCLOAK_URL:-http://localhost:8080}"
REALM="${KEYCLOAK_REALM:-docvault}"
CLIENT="${CLIENT_ID:-docvault-worker}"

SECRET="${CLIENT_SECRET:-$(terraform -chdir="$(dirname "$0")/../infra/terraform/20-realm" output -raw worker_client_secret)}"

response=$(curl -sf -X POST "${KEYCLOAK_URL}/realms/${REALM}/protocol/openid-connect/token" \
  -d "client_id=${CLIENT}" -d "client_secret=${SECRET}" -d grant_type=client_credentials)

echo "$response" | python3 -c '
import base64, json, sys
token = json.load(sys.stdin)["access_token"]
header, payload, _ = token.split(".")
def decode(segment):
    segment += "=" * (-len(segment) % 4)
    return json.loads(base64.urlsafe_b64decode(segment))
print("--- header ---");  print(json.dumps(decode(header), indent=2))
print("--- payload ---"); print(json.dumps(decode(payload), indent=2))
print("--- raw ---");     print(token)
'
