# UC1 — Secure document sharing (RBAC + per-document authorization)

> Keycloak does not store or encrypt your files. It decides **who may do what to
> which file**, which is the half of "secure file sharing" that identity owns.

## Problem

Acme and Globex both use DocVault. Requirements:

- Globex must never see an Acme document — not even its existence.
- Within Acme, engineering writes, legal administers, everyone reads.
- A document's *owner* can share it, regardless of role.

The first two are RBAC. The third is not: it depends on data, so no role can
express it.

## Four questions, four answers

| Question | Answered by | From |
|---|---|---|
| Which tenant are you in? | the API | the token's `groups` path |
| What kind of thing may you do? (read, write, administer) | Keycloak roles → .NET policies | the token's roles |
| Was your sign-in strong enough? (classified only) | the step-up policy | the token's `acr` ([UC3](uc3-step-up-mfa.md)) |
| May you do it to **this** document? | **OpenFGA** | stored relationships + your token's memberships |

The first three are in the token. The fourth cannot be: it depends on the
document. [Chapter 14](../14-fine-grained-authorization.md) has the design and
everything that was measured on the way.

### Tenant scoping

```csharp
var tenant = user.GetTenantContext();   // "/acme/engineering" -> acme
store.ListForTenant(tenant.Tenant, includeClassified: false);
```

From the token, never the request body. And a document in another tenant is a
**404, not 403** — a 403 confirms existence.

### RBAC

```hcl
resource "keycloak_group_roles" "acme_engineering" {
  group_id = keycloak_group.acme_engineering.id
  role_ids = [keycloak_role.api["doc.editor"].id]   # inherits doc.reader from /acme
}
```

```csharp
.AddPolicy(Policies.WriteDocuments, p => p.RequireRole("doc.editor", "doc.admin"))
```

### Per document: OpenFGA

A document is visible to its whole tenant (the default) or **private**: its
owner, and whoever the owner shares it with. Sharing is with a person or a
department of the same tenant.

```
type document
  relations
    define tenant: [tenant]
    define owner: [user]
    define viewer: [user, department#member, tenant#member]
    define in_tenant: member from tenant
    define can_view: (owner or viewer) and in_tenant
    define can_share: owner and in_tenant
    define can_delete: owner and in_tenant
```

OpenFGA stores only the relationships: who owns a document, who it is shared
with, which tenant it belongs to. **It never stores who belongs to a tenant or a
department.** The API sends that with every check, built from the caller's token.
Keycloak stays the only place membership lives, so someone who leaves a
department loses its shares with their next token.

What each role can do, combined with ownership:

| | Tenant document | Private document, not shared with you | Private document, shared with you | Your own document |
|---|---|---|---|---|
| See it | yes | **404** | yes | yes |
| Share it | 403 | 404 | 403 | yes |
| Delete it | `doc.admin` only | **404, even for `doc.admin`** | `doc.admin` only | yes |

An administrator cannot delete a private document they cannot see. The other
way round — an administrator who can see everything — is "break glass", a real
requirement in regulated places, and deliberately not built here.

## Keycloak's Authorization Services: the road not taken

The realm also models documents in Keycloak's own Authorization Services, and
`make assert-authz` checks that model's decisions:

```hcl
resource "keycloak_openid_client_authorization_resource" "document" {
  name                 = "document"
  type                 = "urn:docvault:resources:document"
  scopes               = ["document:view", "document:edit", "document:share", "document:delete"]
  owner_managed_access = true      # the uploader manages access to their own file
}
```

It is not what the API asks. Per-document sharing through it means registering
every document in Keycloak through the Protection API, which takes the resource
server's client secret and `allow_remote_resource_management`, and then the UMA
flow. Keycloak would hold a copy of the document list. OpenFGA holds tuples that
point at documents the API owns, and leaves people and groups in Keycloak.
[Chapter 14](../14-fine-grained-authorization.md#why-not-keycloaks-authorization-services)
has the comparison; the model stays as the worked alternative.

## Try it

```bash
make up && make seed && make api && make web
```

| Sign in as | Expect |
|---|---|
| `alice` (`/acme/engineering`) | 2 Acme documents; the create form, with a choice of visibility |
| `bob` (`/globex`) | 1 Globex document; **no Acme documents at all** |
| `dave` (no groups) | **403** — authenticated, but unauthorized |

Then, as alice, create a document *only for me*, open **Share**, and share it
with `carol`. Sign in as carol in another browser: it appears. Remove the share
and it is gone at her next load. Share it with the department `engineering`
instead, and `erin` (from the LDAP directory) sees it.

Prove the UI is not the control:

```bash
TOKEN=…            # a doc.reader token
curl -X POST http://localhost:5001/documents \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"title":"Nope"}'
# 403 - the API enforces it even though the button was hidden
```

Covered by:
- `AuthorizationTests` and `SharingTests` (`make test`): the API's wiring, over
  a fake OpenFGA that records what it is sent and does not evaluate the model;
- `infra/openfga/docvault.fga.yml` (`make test-model`): the model, on OpenFGA's
  own engine;
- `tools/assert-fga.py` (`make assert-fga`): both together, with real people,
  a directory change, and OpenFGA frozen;
- `tests/e2e-playwright/tests/sharing.spec.ts`: alice and carol in two browsers.

## Notes

- **Roles on groups, not users.** Onboarding is one membership change.
- **Client roles, not realm roles**, for `doc.*` — an unrelated client cannot mint
  itself a `doc.admin` this API would honour.
- **Seeded documents have no owner.** Keycloak picks user ids when it creates
  the realm, so the API cannot record alice as their owner. Nobody can share
  them, and only an administrator can delete them.
- **Local lab and CI only.** Azure runs no OpenFGA: `30-azure` sets
  `Sharing__Mode=TenantOnly`, where every document is visible to its tenant and
  a private one is refused rather than created visible to everyone.
