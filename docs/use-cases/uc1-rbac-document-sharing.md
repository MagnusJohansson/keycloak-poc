# UC1 — Secure document sharing (RBAC + per-resource authorization)

> Keycloak does not store or encrypt your files. It decides **who may do what to
> which file**, which is the half of "secure file sharing" that identity owns.

## Problem

Acme and Globex both use DocVault. Requirements:

- Globex must never see an Acme document — not even its existence.
- Within Acme, engineering writes, legal administers, everyone reads.
- A document's *owner* can share it, regardless of role.

The first two are RBAC. The third is not: it depends on data, so no role can
express it.

## Three layers

| Layer | Answers | Where enforced |
|---|---|---|
| Tenant scoping | which documents exist for you | API, from the `groups` claim |
| RBAC | may you edit *documents* | Keycloak roles → .NET policies |
| Fine-grained | may you edit *this* document | Modelled in Authorization Services; not yet enforced |

### Tenant scoping

```csharp
var tenant = user.GetTenantContext();   // "/acme/engineering" -> acme
store.ListForTenant(tenant.Tenant, includeClassified: false);
```

From the token, never the request body. And deleting another tenant's document
returns **404, not 403** — a 403 confirms existence.

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

### Fine-grained

```hcl
resource "keycloak_openid_client_authorization_resource" "document" {
  name                 = "document"
  type                 = "urn:docvault:resources:document"
  scopes               = ["document:view", "document:edit", "document:share", "document:delete"]
  owner_managed_access = true      # the uploader manages access to their own file
}
```

`owner_managed_access` is what makes "the owner may share it" *expressible*
without hard-coding `if (doc.OwnerId == userId)` in the API — the rule can live
in Keycloak, where it is auditable and changeable without a redeploy.

Expressible, not yet enforced. The model is provisioned, but the API never asks
Keycloak for a decision: doing so means the UMA ticket flow, an outbound call per
decision, and Keycloak on the request path. Today the API does RBAC on the roles
in the token plus tenant scoping from the group path, and there is no share
endpoint. Treat this section as the shape of the answer, not a working feature.

## Try it

```bash
make up && make seed && make api && make web
```

| Sign in as | Expect |
|---|---|
| `alice` (`/acme/engineering`) | 3 Acme documents; the create form appears |
| `bob` (`/globex`) | 1 Globex document; **no Acme documents at all** |
| `dave` (no groups) | **403** — authenticated, but unauthorized |

Prove the UI is not the control:

```bash
TOKEN=…            # a doc.reader token
curl -X POST http://localhost:5001/documents \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"title":"Nope"}'
# 403 - the API enforces it even though the button was hidden
```

Covered by `AuthorizationTests.Tenants_cannot_see_each_others_documents` and
`Reader_cannot_create_a_document`.

## Notes

- **Roles on groups, not users.** Onboarding is one membership change.
- **Client roles, not realm roles**, for `doc.*` — an unrelated client cannot mint
  itself a `doc.admin` this API would honour.
- **`UNANIMOUS` for delete.** Destructive actions should require every attached
  policy to agree, not merely one.
