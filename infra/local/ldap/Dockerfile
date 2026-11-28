# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# The lab's directory: OpenLDAP with a handful of people and groups, for
# Keycloak's LDAP user federation (infra/terraform/20-realm/ldap.tf).
#
# Built here rather than pulled, because the usual OpenLDAP images are either
# unmaintained or ship an OpenLDAP that is out of support. This is Alpine's
# package and a config file you can read in one screen.
#
# The directory is filled at build time and has no volume: every `make up` on a
# fresh container starts from the same people, with the same entryUUIDs, so
# Keycloak's link to each of them survives a rebuild.
FROM mirror.gcr.io/library/alpine:3.24@sha256:f59abe656b692d901e184be3445e084786602108599d2a51d229c0f5a48c71bf

RUN apk add --no-cache openldap openldap-back-mdb openldap-clients openldap-passwd-argon2 openssl \
 && install -d -o ldap -g ldap -m 0700 /var/lib/openldap/openldap-data /run/openldap \
 && install -d -m 0700 /tls/private && install -d -m 0755 /tls/public

COPY slapd.conf /etc/openldap/slapd.conf
COPY seed.sh /tmp/seed.sh
COPY --chmod=0755 entrypoint.sh /usr/local/bin/entrypoint.sh

# Lab passwords, the same ones the README gives for the other demo users. They
# are hashed (Argon2) before they reach the directory; the image history still
# shows them, which is fine for a lab and is why this is not a pattern to copy.
ARG LDAP_USER_PASSWORD=DocVaultLab!2026
ARG LDAP_BIND_PASSWORD=keycloak-bind-lab

RUN sh /tmp/seed.sh > /tmp/seed.ldif \
 && slapadd -f /etc/openldap/slapd.conf -l /tmp/seed.ldif \
 && chown -R ldap:ldap /var/lib/openldap/openldap-data \
 && rm /tmp/seed.sh /tmp/seed.ldif

EXPOSE 636

CMD ["/usr/local/bin/entrypoint.sh"]
