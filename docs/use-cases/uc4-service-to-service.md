# UC4 — Machine-to-machine authentication

> Keycloak does not carry messages between your services. It authenticates the
> services that do, so each one can prove who it is without a shared API key.

## Problem

A nightly job indexes documents. No user is present, so there is no browser, no
redirect and no consent. The usual answer — a long-lived API key in an environment
variable — is bad: it never expires, is copied into logs and screenshots, is
shared between environments, and rotating it means a coordinated redeploy.

## The `client_credentials` grant

The service authenticates **as itself** and receives a short-lived token:

```
worker ──▶ Keycloak  POST /token
                     grant_type=client_credentials
                     client_id=docvault-worker&client_secret=…
       ◀── access_token (5 min)
worker ──▶ API       Authorization: Bearer …
```

The secret is exchanged for a token on demand. A leaked *token* expires in
minutes; the secret itself is rotated centrally without touching the API.

## Configure

```hcl
resource "keycloak_openid_client" "worker" {
  client_id                    = "docvault-worker"
  access_type                  = "CONFIDENTIAL"
  service_accounts_enabled     = true    # this is what enables client_credentials
  standard_flow_enabled        = false   # a daemon has no browser
  direct_access_grants_enabled = false
}
```

The service account is a real principal with a real identity
(`service-account-docvault-worker`) and can hold roles like anyone else:

```hcl
resource "keycloak_openid_client_service_account_role" "worker_reader" {
  service_account_user_id = keycloak_openid_client.worker.service_account_user_id
  role                    = keycloak_role.api["doc.reader"].name
}
```

`doc.reader` and nothing more. Least privilege applies to machine identities
exactly as to human ones — the indexer has no business deleting anything.

## The code

```csharp
if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiry.AddSeconds(-60))
    return _cachedToken;
```

Cache until nearly expired. Fetching per call works but hammers Keycloak; the 60s
margin absorbs clock drift and request latency.

## Try it

```bash
make show-secrets      # copy the worker client secret
cd apps/api-dotnet && dotnet user-secrets set "Keycloak:ClientSecret" "<secret>" --project DocVault.Worker
make worker
```

Or inspect the token directly:

```bash
make token
```

```jsonc
{
  "aud": ["docvault-api", "account"],
  "azp": "docvault-worker",
  "sub": "6dfbeff0-…",                                  // the service account
  "resource_access": { "docvault-api": { "roles": ["doc.reader"] } }
}
```

## The instructive failure

The worker gets **403** from `/documents`. It holds `doc.reader`, but belongs to
no tenant group — so the API cannot scope a query for it.

That is the lesson, not a bug: **a role is not an authorization**. Machine
identities need modelling as deliberately as human ones. Fix it by deciding what
the worker should actually see — a dedicated group, a tenant-scoped token per
pass, or an endpoint designed for cross-tenant batch work with its own realm role.

## Where to keep the secret

| Environment | Store |
|---|---|
| Local | `dotnet user-secrets` — never `appsettings.json` |
| Azure | **Key Vault**, read via managed identity |
| CI | GitHub OIDC federation — no stored secret at all |

`30-azure` provisions the Key Vault and the user-assigned identity with
`Key Vault Secrets User`. The service then holds no credential of its own.

## Beyond shared secrets

- **`client-jwt`** — the client authenticates with a signed assertion instead of a
  secret, so nothing reusable is transmitted.
- **Workload identity federation** — the platform (AKS, GitHub Actions) attests to
  the workload and Keycloak trusts that attestation. No secret exists at all.

Both are configured on the client; the worker code barely changes.
