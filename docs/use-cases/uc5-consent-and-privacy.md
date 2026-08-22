# UC5 — Consent and data minimisation

> Keycloak does not anonymise your data warehouse — but it *can* stop a client
> from ever learning who a user is, which is real privacy engineering.

## Problem

An analytics service reports usage. It has no business knowing *which* individual
did what, and it certainly should not be able to join its records against the main
application's by user id.

Two controls, both free:

1. **Consent** — the user explicitly agrees before the client receives anything.
2. **Pairwise subject identifiers** — the client gets a pseudonym unique to it.

## Pairwise `sub`

Normally every client sees the same `sub` for a user. Two services can therefore
correlate their records perfectly. A pairwise identifier is a one-way hash of the
subject and the client's sector, so:

- The analytics service can still tell two sessions apart (stable per user).
- It **cannot** match its `sub` to the main app's, or to any other client's.

```hcl
resource "keycloak_generic_protocol_mapper" "analytics_pairwise_sub" {
  client_id       = keycloak_openid_client.analytics.id
  protocol_mapper = "oidc-sha256-pairwise-sub-mapper"
  config          = { "salt" = "…" }
}
```

> **No `sectorIdentifierUri`, deliberately.** Keycloak *fetches* that URL while
> creating the mapper and refuses if it is unreachable — which would make the
> realm module depend on the API already running, a chicken-and-egg problem since
> the API needs the realm. A sector identifier is only required when a client's
> redirect URIs span multiple hosts. This one has a single host, so Keycloak
> derives the sector from it. The API exposes `/analytics/sector-identifier` for
> when you do need it.

## Consent

```hcl
resource "keycloak_openid_client" "analytics" {
  consent_required = true
}

resource "keycloak_openid_client_scope" "analytics_read" {
  name                = "analytics:read"
  consent_screen_text = "View anonymised usage statistics"
}

resource "keycloak_openid_client_optional_scopes" "analytics" {
  optional_scopes = [keycloak_openid_client_scope.analytics_read.name]
}
```

**Optional**, not default. A default scope is granted silently on every login; an
optional one is granted only if the client asks *and* the user agrees. The user
can revoke it later from their account console, and the next token simply lacks
the scope.

## Scope, not role

```csharp
.AddPolicy(Policies.Analytics, p => p.RequireAssertion(ctx =>
    ctx.User.FindFirst("scope")?.Value.Split(' ').Contains("analytics:read") == true));
```

A **role** says what the user may do. A **scope** says what the client was
authorised to ask for on their behalf. Here consent governs access, so the scope
is the right gate — a `doc.admin` with no consented scope is refused
(`Analytics_requires_the_consented_scope_not_merely_a_role`).

## Minimise the response too

```csharp
return Results.Ok(new {
    totalDocuments      = documents.Count,
    classifiedDocuments = documents.Count(d => d.IsClassified),
    distinctOwners      = documents.Select(d => d.OwnerUsername).Distinct().Count(),
});
```

Counts, never rows. Identity controls limit *who* asks; the endpoint shape limits
*what* can be learned. Both are needed — a perfectly pseudonymised token still
leaks everything if the endpoint returns full records.

## Try it

```bash
make token   # then compare `sub` across clients
```

Request a token for `docvault-analytics` and one for `docvault-web-react` as the
same user: the `sub` values differ and cannot be linked.

## Honest limits

- Pairwise `sub` stops correlation **by user id**. It does nothing about email,
  username, or IP — so do not also emit those. Drop `profile` and `email` from
  this client's scopes for a real deployment.
- The salt is a secret. Anyone holding it and the subject list can recompute the
  mapping. Treat it like a signing key.
- This is pseudonymisation, not anonymisation. Under GDPR pseudonymised data is
  still personal data.
