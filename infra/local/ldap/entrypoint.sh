#!/bin/sh
# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# Makes the directory's TLS certificate on first start, then runs slapd.
#
# The key is made here, not at build time and not committed: a private key in a
# repository or an image is one everybody has. It lives in a volume only this
# container mounts. The certificate alone goes to a second volume, which the
# Keycloak container mounts as a truststore.
set -eu

KEY=/tls/private/ldap.key
CERT=/tls/public/ldap.crt

if [ ! -s "$KEY" ] || [ ! -s "$CERT" ]; then
  # The name must be the one Keycloak connects to: Java checks it.
  openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes \
    -days 825 -subj "/CN=ldap" -addext "subjectAltName=DNS:ldap" \
    -keyout "$KEY" -out "$CERT" 2>/dev/null
fi
# slapd gives up root before it reads the key, so the key is the ldap user's.
chown -R ldap:ldap /tls/private && chmod 0700 /tls/private && chmod 0400 "$KEY"
chmod 0755 /tls/public && chmod 0644 "$CERT"

# ldaps:/// for Keycloak, on the compose network only (no port is published).
#           There is no plain ldap:/// listener: a password cannot cross the
#           network unencrypted even by misconfiguration.
# ldapi:/// a Unix socket inside the container. Root there is the directory's
#           administrator (see slapd.conf), so there is no admin password.
# -d stats  logs each bind and search to stdout, and keeps slapd in the foreground.
exec slapd -u ldap -g ldap -f /etc/openldap/slapd.conf \
  -h "ldaps:/// ldapi://%2Frun%2Fopenldap%2Fldapi" -d stats
