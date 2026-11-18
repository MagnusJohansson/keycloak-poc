#!/bin/sh
# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# Prints the directory's contents as LDIF. Run once, when the image is built.
#
# entryUUID is set by hand. Keycloak links each imported user to this value, so
# a fixed one keeps the link when the image is rebuilt.
set -eu

hash() { slappasswd -o module-path=/usr/lib/openldap -o module-load=argon2 -h '{ARGON2}' -s "$1"; }

BASE="dc=docvault,dc=test"
USER_HASH=$(hash "$LDAP_USER_PASSWORD")
BIND_HASH=$(hash "$LDAP_BIND_PASSWORD")

cat <<LDIF
dn: $BASE
objectClass: dcObject
objectClass: organization
dc: docvault
o: DocVault lab directory

dn: ou=people,$BASE
objectClass: organizationalUnit
ou: people

dn: ou=groups,$BASE
objectClass: organizationalUnit
ou: groups

dn: ou=services,$BASE
objectClass: organizationalUnit
ou: services

# The account Keycloak binds with. It is not under ou=people, so it is not a
# user Keycloak can import or sign in.
dn: cn=keycloak,ou=services,$BASE
objectClass: applicationProcess
objectClass: simpleSecurityObject
cn: keycloak
userPassword: $BIND_HASH

LDIF

person() { # uid, given name, surname, mail, entryUUID
cat <<LDIF
dn: uid=$1,ou=people,$BASE
objectClass: inetOrgPerson
uid: $1
givenName: $2
sn: $3
cn: $2 $3
mail: $4
userPassword: $USER_HASH
entryUUID: $5

LDIF
}

#      uid   given  surname   mail              entryUUID
person erin  Erin   Eriksson  erin@acme.test    0e1d0001-d0c0-4a01-8000-000000000001
person frank Frank  Fischer   frank@globex.test 0e1d0001-d0c0-4a01-8000-000000000002
person grace Grace  Gomez     grace@acme.test   0e1d0001-d0c0-4a01-8000-000000000003

# Groups say who belongs where. What a group may do is decided in Keycloak
# (roles-groups.tf), not here. Keycloak matches these to its own groups by name
# and accepts only the names listed in ldap.tf. "engineering" is a member of
# "acme" because that is how a directory is usually laid out; Keycloak does not
# use the nesting.
cat <<LDIF
dn: cn=engineering,ou=groups,$BASE
objectClass: groupOfNames
cn: engineering
member: uid=erin,ou=people,$BASE

dn: cn=acme,ou=groups,$BASE
objectClass: groupOfNames
cn: acme
member: cn=engineering,ou=groups,$BASE

dn: cn=globex,ou=groups,$BASE
objectClass: groupOfNames
cn: globex
member: uid=frank,ou=people,$BASE
LDIF
