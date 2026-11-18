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
| `erin` | `engineering` | `/acme/engineering` | the happy path: editor in Acme |
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
  as a truststore. The certificate is not a CA: its key cannot sign a
  certificate for another name.
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

Keycloak takes a person's username and email address once, when it first meets
them, and reads their names from the directory each time (see the traps).

Groups are mapped by one mapper. It reads a person's directory groups each
time; it does not copy them:

```hcl
resource "keycloak_ldap_group_mapper" "directory" {
  ldap_groups_dn            = "ou=groups,${var.ldap_base_dn}"
  group_object_classes      = ["groupOfNames"]
  membership_ldap_attribute = "member"

  # only these directory groups count
  groups_ldap_filter = "(|(cn=acme)(cn=engineering)(cn=globex))"

  mode                       = "READ_ONLY"
  preserve_group_inheritance = true
  groups_path                = "/"
}
```

The realm's groups are made by Terraform, with their roles. A directory group
is matched to a realm group **by name alone**: the directory's `engineering` is
the realm's `/acme/engineering`, wherever it sits in the directory. So whoever
can put a person in the directory's `engineering` decides who is an Acme
editor. That is the point of a directory, and it is worth saying plainly. The
filter limits which directory groups are looked at, and with it a directory
group the realm does not list grants nothing.

## Verify

```bash
make up && make seed && make api
make assert-ldap        # 51 checks; changes the directory and puts it back
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
  email address, cannot sign in as the realm's alice. A person Keycloak already
  knows, who then takes alice's email address or is renamed `alice` in the
  directory, keeps her own name and address in her token.
- **Groups:** a directory group the realm does not list adds nothing to a token
  and creates nothing in the realm, also when it is named like a realm group.
- **Lockout:** the realm's brute-force protection covers directory people.
- **Outage:** with the directory not answering, its people are refused and
  local users sign in as usual. When it answers again, so does sign-in.
- **The directory's own doors:** anonymous access, a write by Keycloak's
  account, and a connection without TLS are all refused.
- **The certificate is checked:** Keycloak refuses the same server under a
  second name its certificate does not carry, and the certificate is not a CA.

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
- **Without a filter, the directory can create groups in the realm.** A person
  in a directory group the realm had never heard of got that group created in
  the realm at her next sign-in, outside Terraform, and `make plan` stayed
  empty. With two top-level groups in her token the API answered 403
  "Ambiguous tenant". A directory group named `legal` gave its member a realm
  group of that name. One sequence left a second, top-level `/engineering`
  behind that outlived the directory being put back. `groups_ldap_filter`
  names the three groups that count. Removing the filter again with Terraform
  did not clear it in Keycloak; it had to be cleared through the admin API.
- **Re-reading the username and email lets a person borrow someone else's.**
  Keycloak refuses to import a directory person whose email belongs to a local
  user. It did not refuse the same change afterwards: with
  `always_read_value_from_ldap`, a person already imported who then took
  alice's address in the directory carried it in her token, marked verified,
  and one renamed to `alice` carried that as `preferred_username`. She could
  not sign in as alice and gained no access, but anything that names a person
  by email or username, such as a log line, would name the wrong one. The
  username and email are now taken once, at import. The cost is the other side
  of the same coin: a legitimate change of address in the directory does not
  reach Keycloak.
- **A refused import can lock the person out after the conflict is fixed.** On
  a new Keycloak, after `mallory` was refused for carrying alice's email
  address, a corrected `mallory` entry still could not sign in: Keycloak
  answered "invalid credentials" without asking the directory. Clearing
  Keycloak's user cache let her in. It fails closed, and it happens with the
  federation's own cache off. It did not happen on my longer-lived lab, and I
  have not established what differs.
- **`openssl req -x509` makes a CA certificate.** Keycloak trusts the
  directory's certificate file directly, so with `CA:TRUE` the directory's key
  could have signed a certificate for any name and Keycloak accepted it. The
  certificate is now made with `CA:FALSE` and server-authentication usage only.
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
  says `LDAP server is unavailable`. While the directory's name resolves and it
  does not answer, a sign-in takes about ten seconds to fail: two connection
  timeouts of five.
- **Stopping the container is not the same outage.** `docker compose stop` also
  removes the directory's name from Docker's DNS. Java caches a failed lookup
  for ten seconds, so sign-ins went on failing for up to ten seconds after the
  directory was back. That looked like slow recovery in Keycloak and was not.
  The check freezes the container instead, which keeps the name.
- **The realm's lockout trips on a check that fails sign-ins on purpose.** Two
  failed sign-ins within a second lock an account for a minute. The check
  clears the counters before each sign-in that is meant to succeed.

## What it does not do

- **Stop a token that is already out.** The API validates a token on its own;
  an access token issued before a change works until it expires, which is five
  minutes here. `make assert-ldap` states this as a check, not as a failure.
- **Keep a person's identity across a delete and restore.** Deleted and added
  back with the same `entryUUID`, erin signs in again and is a new user to
  Keycloak, with a new `sub`. Anything keyed on the old one no longer matches.
- **Follow a change of username or email.** Both are taken once, when Keycloak
  first meets a person (see the traps).
- **End a session when a password changes.** A password changed in the
  directory is needed for the next sign-in; an existing session still
  refreshes.
- **Undo what an administrator grants in Keycloak.** A directory person can be
  added to a realm group, or given a role, in Keycloak itself. That grant is
  Keycloak's and stays when the directory takes her out of her groups.
- **Write to the directory.** No password change, no self-registration, no
  profile edits from Keycloak.
- **Account state from the directory.** OpenLDAP has no "disabled" flag that
  Keycloak reads here, and no password policy or lockout is configured in it.
  A leaver in this lab is someone deleted or removed from their groups.
- **Scheduled sync.** No periodic sync runs. People are imported the first time
  Keycloak meets them: at sign-in, or when an administrator lists users or a
  group's members.
- **Active Directory.** This is OpenLDAP with `inetOrgPerson` and
  `groupOfNames`. AD differs in attributes, in how accounts are disabled, and
  in Kerberos, none of which is here.
- **A certificate authority.** The directory's certificate is self-signed and
  trusted directly. There is no rotation.
- **Azure.** The Azure deployment has no directory.
