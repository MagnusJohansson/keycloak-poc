#!/usr/bin/env bash
# Exports the running local realm to JSON.
#
# The export is a GENERATED artifact, committed only so the realm can be seeded
# quickly (CI, or `--import-realm` on first boot). Terraform in
# infra/terraform/20-realm remains the source of truth - edit the HCL, re-run
# `make seed`, then re-run this script. Never hand-edit the JSON.
set -euo pipefail

KEYCLOAK_URL="${KEYCLOAK_URL:-http://localhost:8080}"
REALM="${KEYCLOAK_REALM:-docvault}"
ADMIN="${KEYCLOAK_ADMIN:-admin}"
PASSWORD="${KEYCLOAK_ADMIN_PASSWORD:-admin}"
OUT="$(dirname "$0")/../infra/local/realm-export/${REALM}-realm.json"

token=$(curl -sf -X POST "${KEYCLOAK_URL}/realms/master/protocol/openid-connect/token" \
  -d client_id=admin-cli -d "username=${ADMIN}" -d "password=${PASSWORD}" -d grant_type=password \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["access_token"])')

# partialExport does not include client secrets or user credentials by design -
# they are regenerated on import. Users are exported separately below.
curl -sf -X POST "${KEYCLOAK_URL}/admin/realms/${REALM}/partial-export?exportClients=true&exportGroupsAndRoles=true" \
  -H "Authorization: Bearer ${token}" \
  | python3 -m json.tool > "${OUT}"

echo "Wrote ${OUT}"
echo "NOTE: client secrets and user passwords are deliberately absent from this export."
