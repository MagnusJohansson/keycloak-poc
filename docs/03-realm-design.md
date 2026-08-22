# 4. Realm design

The DocVault realm, and why each decision was made. Source:
`infra/terraform/20-realm/`.

## The scenario

A multi-tenant document workspace. Deliberately dull, because a dull domain
exercises the whole IAM surface without the domain itself needing explanation:
tenants, departments, RBAC, per-resource ownership, step-up auth, machine
identities, consent, and audit.

## Structure

```
realm: docvault
├── groups
│   ├── /acme                      tenant
│   │   ├── /acme/engineering      doc.editor
│   │   └── /acme/legal            doc.admin
│   └── /globex                    tenant
├── realm role
│   └── platform-admin             crosses tenants; deliberately rare
├── client roles (on docvault-api)
│   ├── doc.reader / doc.editor / doc.admin
└── clients
    ├── docvault-api               confidential, authz services, no login flows
    ├── docvault-web-react         public + PKCE
    ├── docvault-web-vue           public + PKCE
    ├── docvault-mobile            public + PKCE, custom scheme
    ├── docvault-desktop           public + PKCE, loopback
    ├── docvault-worker            confidential, client_credentials
    └── docvault-analytics         confidential, consent, pairwise sub
```

## Decisions worth explaining

### Tenants are groups, not realms

The alternative — one realm per tenant — gives stronger isolation but costs a
realm per customer, separate signing keys, and a login page that must first ask
"who are you with?". Groups keep one issuer and one login experience, and the
tenant boundary is enforced by the API from the group path.

Choose realm-per-tenant when tenants need genuinely separate identity providers
or key material. Choose groups when they are customers of one product.

### Roles ride on groups, never on users

`keycloak_group_roles` attaches roles to groups; users only get memberships.
Onboarding is then one membership change. The single exception is `carol`, who
holds `platform-admin` directly — deliberately, because platform operators are
not a tenant property.

### Tenant travels as a group *path*

Group attributes (`tenant`, `department`) are set in Terraform but **never reach
the token** — Keycloak has no mapper for group attributes. The token carries
`groups: ["/acme/engineering"]`, and `TenantClaimExtensions` parses it.

A user in two tenants throws rather than picking the first. Guessing there would
be a cross-tenant data leak; the correct product answer is a tenant switcher that
mints a tenant-scoped token.

### Short access tokens, longer sessions

```hcl
access_token_lifespan    = "5m"
sso_session_idle_timeout = "30m"
refresh_token_max_reuse  = 0
```

Five minutes bounds the damage from a leaked access token, while the SSO session
keeps the user from re-authenticating constantly. `refresh_token_max_reuse = 0`
turns on reuse detection: a replayed refresh token kills the whole session, which
is the standard defence against refresh-token theft.

### ACR → LoA map

```hcl
attributes = {
  "acr.loa.map" = jsonencode({ bronze = 1, silver = 2 })
}
```

This is the contract between the authentication flow and the API. The API demands
`acr=silver`; Keycloak resolves that to LoA 2, which the browser flow implements
as password + OTP.

**Without this map, `acr_values` is silently ignored** — no error, no warning, and
a step-up demo that appears to work while proving nothing. Confirm it took effect:

```bash
curl -s http://localhost:8080/realms/docvault/.well-known/openid-configuration \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["acr_values_supported"])'
# ['bronze', 'silver', '0', '1', '2']
```

### Mappers are attached per client, not via a shared scope

`keycloak_openid_client_default_scopes` is **authoritative** — it replaces the
entire default-scope list, so you must restate every built-in (`acr`, `basic`,
`email`, `profile`, `roles`, `web-origins`, plus `organization` when that feature
is on). That list differs between Keycloak versions and between the local
container and a managed cluster, which would break the "same HCL everywhere"
property. Per-client mappers are more verbose and completely portable.

### Fine-grained authorization lives in Keycloak

RBAC answers "may this user edit documents?" but not "may they edit *this*
document?" — that depends on data. Authorization Services move the second
decision into Keycloak, where it is auditable and changeable without redeploying
the API. See `authz-documents.tf`.

## Applying it

```bash
make seed                              # local
make seed-azure                        # Keycloak on Azure, same module
make plan                              # diff without applying
```

68 resources. Two ordering rules the module encodes for you:

- **Authentication executions must be created in order.** Keycloak assigns
  priority by creation sequence, and Terraform parallelises by default, so
  `authn-stepup.tf` uses explicit `depends_on` chains. Without them the flow comes
  out shuffled and authentication breaks confusingly.
- **`*_scopes` resources key off the client's internal UUID** (`.id`), not its
  OAuth `client_id` string. Passing the latter fails with "client with id … does
  not exist", which reads like a race but is simply the wrong identifier.

---

Next: [5. Web integration](04-integration-web.md).
