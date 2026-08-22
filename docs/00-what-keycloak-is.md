# 1. What Keycloak is

## In one sentence

Keycloak is an open-source **identity and access management** server: it
authenticates users, decides what they are allowed to do, and issues signed
tokens saying so.

It is a CNCF-graduated project, originally from Red Hat, and it is the engine
behind Red Hat build of Keycloak and most "managed Keycloak" offerings. Running
it yourself is the default assumption of this lab.

## What it does, and what it does not

Keycloak answers two questions:

- **Authentication** — who is this? (password, OTP, passkey, or "ask Microsoft")
- **Authorization** — what may they do? (roles, groups, scopes, fine-grained policies)

It does **not** encrypt your application's data, store your files, or transport
your messages. This distinction matters because the two categories fail
differently: an IAM outage locks everyone out; an encryption product's failure
exposes data.

Where Keycloak *does* touch privacy is worth knowing, because it is easy to
miss: it can issue each client a **different, non-reversible `sub`** for the same
person, so two services holding "the same user" cannot prove it. That is a real
privacy control and it costs nothing — see
[uc5](use-cases/uc5-consent-and-privacy.md).

## What you get

| Area | Included |
|---|---|
| Protocols | OpenID Connect, OAuth 2.0, SAML 2.0 |
| Authentication | Passwords, TOTP, WebAuthn passkeys, magic links, recovery codes |
| Federation | Any OIDC or SAML provider; LDAP and Active Directory user federation |
| Social login | Google, GitHub, Microsoft, Apple and ~20 more, built in |
| Authorization | Realm and client roles, groups, client scopes, and fine-grained Authorization Services (resources, scopes, policies, permissions) |
| Multi-tenancy | Realms, groups, and the Organizations feature |
| Audit | User and admin event streams, with pluggable listeners |
| Extensibility | Service Provider Interfaces for custom authenticators, mappers, storage and event listeners; themable login pages |
| Operations | Admin REST API, realm import/export, metrics and health endpoints |

All of it is Apache-2.0 licensed, with no per-user cost. That absence changes
architecture: with no MAU metering there is no incentive to share accounts or to
keep users out of the IdP.

## The cost of running it

Keycloak is free to license and not free to operate. Realistically you own:

- **Upgrades.** Roughly quarterly, with occasional breaking changes.
- **CVE response** for Keycloak, the JVM and the base image.
- **The database.** Since 26.1 it is also the cluster-discovery mechanism, so
  it is a hard dependency for high availability, not just for storage.
- **Backups you have actually restored**, not merely configured.
- **Being on call.** If Keycloak is down, nobody can log in to anything — an
  authentication outage is a total outage.

This is the honest trade against a managed IAM service. The lab makes it
concrete: [09-deploying-on-azure](09-deploying-on-azure.md) is a working Azure
deployment with a production-hardening checklist of the things it does *not* yet do.

## When Keycloak is and is not the right answer

| Situation | Reach for |
|---|---|
| You need standards-based SSO, own your identity data, and have (or want) the ops capacity | **Keycloak** |
| Everything is Microsoft and all users are staff already in Entra ID | **Entra ID directly** — Keycloak would just add a hop |
| You want the simplest possible hosted IdP and accept per-user pricing | Auth0, Clerk, WorkOS |
| You want Keycloak's capability without operating it | A managed Keycloak vendor, or Red Hat build of Keycloak |
| Regulatory requirement to run inside your own subscription | **Keycloak, self-hosted** |

A useful property either way: because Keycloak is open source with a documented
realm export format, the decision stays reversible. This repo leans on that —
the same realm definition applies to a local container and to Azure without
modification.

---

Next: [2. Keycloak concepts](01-keycloak-concepts.md) — realms, clients, roles vs
groups vs scopes, and what is actually inside a token.
