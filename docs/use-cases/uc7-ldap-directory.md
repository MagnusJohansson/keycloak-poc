# UC7 — People from an LDAP directory

> Local lab and CI only. The directory is a small OpenLDAP container; the Azure
> deployment has none.

## Problem

Globex and part of Acme keep their staff in an LDAP directory. Those people
should sign in to DocVault with the password they already have. DocVault should
not hold a copy of that password, and when the directory says someone has moved
or left, DocVault should stop treating them as before.

## Shape

```
            sign in as erin
user ──▶ Keycloak (docvault realm) ──ldaps──▶ directory (OpenLDAP)
              │                                  ou=people   erin, frank, grace
              │  1. find uid=erin                ou=groups   acme ▸ engineering, globex
              │  2. bind as erin with the password she typed
              │  3. read her groups
              │
              └── issues ITS OWN token: groups ["/acme/engineering"], roles from the realm
```

The split is the point of the design:

| Question | Answered by |
|---|---|
| Who is this, and is the password right? | the directory |
| Where do they belong? | the directory's groups |
| What does belonging there allow? | this realm: roles ride on groups (`roles-groups.tf`) |

The API does not change, and cannot tell erin from alice: both arrive with a
token from the one issuer it trusts.

## The directory

`infra/local/ldap/` builds it: Alpine's OpenLDAP package, one config file, and a
seed script. It is built here because the commonly used OpenLDAP images are
unmaintained or ship an OpenLDAP that is out of support.

| Person | Directory groups | In Keycloak | Shows |
|---|---|---|---|
| `erin` | `engineering`, which is a member of `acme` | `/acme/engineering` | the happy path: editor in Acme |
| `frank` | `globex` | `/globex` | tenant isolation still holds |
| `grace` | none | no group | signs in, and the API answers 403 |

They share the lab password with the other demo users. `make ldap-search`
prints the directory.

It is deliberately closed:

- **No anonymous access.** Nobody reads it without binding.
- **Keycloak's account can read people and groups, and nothing else.** It cannot
  write, and it cannot read a password. Nor can anyone: a password can be
  checked by binding and never read.
- **Passwords are stored as Argon2 hashes.**
- **TLS only.** There is no plain `ldap://` listener, so a password cannot cross
  the network unencrypted even by misconfiguration. The certificate and key are
  made on first start, in the container. The key stays in a volume only the
  directory mounts; the certificate goes to a second volume that Keycloak mounts
  as a truststore.
- **No port is published.** Only Keycloak, on the compose network, reaches it.
- **No administrator password.** Root inside the container, over a local socket,
  is the administrator. Nothing on the network can be.

## Configure

`infra/terraform/20-realm/ldap.tf`. It exists only when `ldap_connection_url`
is set, which the local lab does and the Azure deployment does not.

```hcl
resource "keycloak_ldap_user_federation" "directory" {
  connection_url  = var.ldap_connection_url   # ldaps://ldap:636
  bind_dn         = var.ldap_bind_dn
  bind_credential = var.ldap_bind_credential

  users_dn                = "ou=people,${var.ldap_base_dn}"
  user_object_classes     = ["inetOrgPerson"]
  username_ldap_attribute = "uid"
  uuid_ldap_attribute     = "entryUUID"

  edit_mode      = "READ_ONLY"   # Keycloak never writes to the directory
  import_enabled = true

  delete_default_mappers = true  # see the traps

  cache {
    policy = "NO_CACHE"          # see the traps
  }
}
```

Groups are mapped by one mapper. It reads a person's groups from the directory
each time; it does not copy them:

```hcl
resource "keycloak_ldap_group_mapper" "directory" {
  ldap_groups_dn            = "ou=groups,${var.ldap_base_dn}"
  group_object_classes      = ["groupOfNames"]
  membership_ldap_attribute = "member"

  mode                       = "READ_ONLY"
  preserve_group_inheritance = true
  groups_path                = "/"
}
```

The groups themselves stay this realm's. Nothing is synced from the directory:
`/acme/engineering` already exists, with its roles, and the directory's
`engineering` (a member of its `acme`) resolves to it. A directory group with
no counterpart in the realm grants nothing.

## Verify

```bash
make up && make seed && make api
make assert-ldap        # 42 checks; changes the directory and puts it back
```

It signs in through the real login page as people who exist only in the
directory, and then changes the directory under Keycloak:

- **Sign-in:** erin, frank and grace get the groups and roles in the table
  above, and the API treats them accordingly.
- **Keycloak holds no password:** its database has no credential row for erin,
  and it refuses to change her name or set a password.
- **A mover:** erin is taken out of `engineering`. Her next refreshed token has
  no group and no roles, and the API answers 403.
- **A leaver:** erin is deleted. Her session cannot be refreshed, she cannot
  sign in, and Keycloak drops its copy of her.
- **No takeover:** a directory entry named `alice`, or one carrying alice's
  email address, cannot sign in as the realm's alice.
- **Outage:** with the directory stopped, its people are refused and local
  users sign in as usual.
- **The directory's own doors:** anonymous access, a write by Keycloak's
  account, and a connection without TLS are all refused.
- **The certificate is checked:** Keycloak refuses the same server under a
  second name its certificate does not carry.

CI runs it in the realm job, against a Keycloak and a directory that are both
new on every run.

## Traps this found

Each of these produced no error at the point of the mistake.

- **With Keycloak's default cache, a mover and a leaver keep their access.**
  Keycloak caches a federated user, groups included. With the default policy,
  erin went on getting `/acme/engineering` and `doc.editor` after she was taken
  out of the group: on refresh, and on a completely new sign-in. Deleted from
  the directory, she could no longer sign in (the password check goes to the
  directory), but her existing session kept refreshing. Nothing complains; the
  sign-in works. `cache { policy = "NO_CACHE" }` makes Keycloak ask the
  directory each time. That costs a directory round trip on every token, which
  a real deployment would weigh against a short `MAX_LIFESPAN` instead.
  `make assert-ldap` fails six checks with the default.
- **Terraform's group membership resource stops working once there is a
  directory.** `keycloak_group_memberships` owns a group's whole member list.
  People from the directory are members too, so every plan wanted to remove
  them, and every apply failed with `Not possible to delete LDAP group mappings
  as mapper mode is READ_ONLY`. The read-only mode is what saved the directory's
  people here; with a writable mapper the apply would have gone through. The
  demo users are now put in their groups one user at a time
  (`keycloak_user_groups`, not exhaustive), and the old resources are dropped
  from state with `removed` blocks so that an existing realm upgrades without
  alice leaving her group on the way.
- **Keycloak's default mapper takes the first name from `cn`.** In a directory
  `cn` is the full name, so erin's first name was "Erin Eriksson". The default
  mappers are deleted and four explicit ones replace them.
- **Keycloak reads its truststore once, at start.** The directory writes its
  certificate on first start, so the Keycloak container waits for the directory
  to be healthy. A certificate that arrives later is not picked up until
  Keycloak restarts, and Keycloak does not connect to a directory whose
  certificate it does not trust.
- **slapd gives up root before it reads its key.** A key owned by root is the
  cautious default and makes the server exit at start with `Permission denied`.
- **A directory outage looks like wrong passwords in Keycloak's events.** The
  event is `LOGIN_ERROR` with `invalid_user_credentials`. Only the server log
  says `LDAP server is unavailable`. A sign-in during the outage also takes
  about ten seconds to fail: two connection timeouts of five. And recovery is
  not always immediate: in roughly one local run in five, the first sign-in
  after the directory returned still failed, and it took about ten seconds
  more. I have not established why.

## What it does not do

- **Stop a token that is already out.** The API validates a token on its own;
  an access token issued before a change works until it expires, which is five
  minutes here. `make assert-ldap` states this as a check, not as a failure.
- **Keep a person's identity across a delete and restore.** Deleted and added
  back with the same `entryUUID`, erin signs in again and is a new user to
  Keycloak, with a new `sub`. Anything keyed on the old one no longer matches.
- **Write to the directory.** No password change, no self-registration, no
  profile edits from Keycloak.
- **Account state from the directory.** OpenLDAP has no "disabled" flag that
  Keycloak reads here, and no password policy or lockout is configured in it.
  A leaver in this lab is someone deleted or removed from their groups.
- **Scheduled sync.** People are imported the first time Keycloak meets them: at
  sign-in, or when an administrator lists a group's members. No periodic sync
  runs, so the admin console does not list everyone in the directory.
- **Active Directory.** This is OpenLDAP with `inetOrgPerson` and
  `groupOfNames`. AD differs in attributes, in how accounts are disabled, and
  in Kerberos, none of which is here.
- **A certificate authority.** The directory's certificate is self-signed and
  trusted directly. There is no rotation.
- **Azure.** The Azure deployment has no directory.
