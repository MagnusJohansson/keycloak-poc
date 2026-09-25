#!/usr/bin/env bash
# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# Asserts that the Authorization Services model grants what it claims to.
#
# The API never calls this model at runtime (see the STATUS note in
# infra/terraform/20-realm/authz-documents.tf), so nothing else would notice if
# it were wrong. It WAS wrong: the permissions were created without
# `type = "scope"`, which made them resource permissions that apply to every
# scope, and with the UNANIMOUS strategy an editor was refused even `view`.
# A model nobody calls is a model nobody tests - this is the test.
#
# Uses the admin policy evaluator, so it needs no user tokens and no browser.
# Usage: tools/assert-authz-model.sh   (against the running local lab)

set -euo pipefail

KEYCLOAK_URL="${KEYCLOAK_URL:-http://localhost:8080}"
REALM="${KEYCLOAK_REALM:-docvault}"
ADMIN="${KEYCLOAK_ADMIN:-admin}"
PASSWORD="${KEYCLOAK_ADMIN_PASSWORD:-admin}"

json() { python3 -c "import json,sys; print(eval(sys.argv[1], {'d': json.load(sys.stdin)}))" "$1"; }

token=$(curl -sf -X POST "${KEYCLOAK_URL}/realms/master/protocol/openid-connect/token" \
  -d client_id=admin-cli -d "username=${ADMIN}" -d "password=${PASSWORD}" -d grant_type=password \
  | json 'd["access_token"]')
admin() { curl -sf -H "Authorization: Bearer ${token}" "$@"; }

api=$(admin "${KEYCLOAK_URL}/admin/realms/${REALM}/clients?clientId=docvault-api" | json 'd[0]["id"]')
evaluate="${KEYCLOAK_URL}/admin/realms/${REALM}/clients/${api}/authz/resource-server/policy/evaluate"

failures=0
expect() { # user resource scope PERMIT|DENY
  local user=$1 resource=$2 scope=$3 want=$4 id got
  id=$(admin "${KEYCLOAK_URL}/admin/realms/${REALM}/users?username=${user}&exact=true" | json 'd[0]["id"]')
  got=$(admin -X POST "$evaluate" -H 'Content-Type: application/json' \
    -d "{\"userId\":\"${id}\",\"resources\":[{\"name\":\"${resource}\",\"scopes\":[{\"name\":\"${scope}\"}]}],\"context\":{\"attributes\":{}}}" \
    | json 'd["status"]')
  if [ "$got" = "$want" ]; then
    echo "ok    ${user} ${resource}#${scope} -> ${got}"
  else
    echo "FAIL  ${user} ${resource}#${scope} -> ${got}, expected ${want}"
    failures=$((failures + 1))
  fi
}

# alice: doc.editor. bob: doc.reader (other tenant; tenancy is the API's job, not this model's).
# carol: doc.admin. dave: no roles.
expect alice document document:view   PERMIT   # the case that was broken
expect alice document document:edit   PERMIT
expect alice document document:delete DENY
expect bob   document document:view   PERMIT
expect bob   document document:edit   DENY
expect carol document document:delete PERMIT
expect dave  document document:view   DENY
expect alice classified-document document:view DENY
expect carol classified-document document:view PERMIT

[ "$failures" -eq 0 ] || { echo "${failures} authorization decision(s) wrong"; exit 1; }
echo "Authorization model grants what it claims to."
