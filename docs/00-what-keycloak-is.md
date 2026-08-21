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

The original brief for this repo ([PRD.md](PRD.md)) initially framed the goal as
"secure data sharing and privacy" with use cases such as *encrypted messaging*
and *privacy-preserving data analytics*. Those describe a different category of
product. They have been re-expressed here as things Keycloak genuinely does:

| Original framing | What this lab actually builds |
|---|---|
| Secure file sharing | RBAC plus per-document ownership via Keycloak Authorization Services ([uc1](use-cases/uc1-rbac-document-sharing.md)) |
| Encrypted messaging | Machine-to-machine auth: `client_credentials` and service accounts ([uc4](use-cases/uc4-service-to-service.md)) — the authentication layer, not the encryption |
| Privacy-preserving analytics | Consent plus data minimisation: optional scopes and **pairwise subject identifiers**, so an analytics client cannot correlate a user against any other client ([uc5](use-cases/uc5-consent-and-privacy.md)) |

The last one is worth dwelling on, because it is real privacy engineering — just
not the kind the brief imagined. Keycloak can issue each client a *different*,
non-reversible `sub` for the same person, so two services holding "the same
user" cannot prove it. That is a meaningful privacy control, and it is free.

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
