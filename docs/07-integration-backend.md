# 8. Backend integration (.NET 10)

Source: `apps/api-dotnet/`.

## Validation

```csharp
options.Authority = configuration["Keycloak:Authority"];   // the realm issuer
options.Audience  = "docvault-api";
options.MapInboundClaims = false;

options.TokenValidationParameters = new TokenValidationParameters
{
    ValidateIssuer = true, ValidateAudience = true,
    ValidateLifetime = true, ValidateIssuerSigningKey = true,
    ClockSkew = TimeSpan.FromSeconds(30),
    ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSsaPssSha256],
    NameClaimType = "preferred_username",
    RoleClaimType = ClaimTypes.Role,
};
```

Four of these are load-bearing and easy to get wrong:

- **`MapInboundClaims = false`.** .NET's legacy claim mapping rewrites JWT claims
  into long WS-Federation URIs — `sub` becomes `nameidentifier`, and
  `realm_access` becomes awkward to find. Turning it off keeps the claims exactly
  as Keycloak issued them.
- **`ClockSkew = 30s`.** The default is **five minutes**, which meaningfully
  extends the useful life of a stolen token. 30s covers realistic drift.
- **`ValidAlgorithms`.** Pinning to RSA is the defence against `alg: none` and
  HMAC-confusion attacks, where an attacker signs a token using the *public* key
  as an HMAC secret.
- **`Audience`.** Requires the audience mapper in `20-realm/mappers.tf`. Without
  it every request 401s with "The audience … is invalid".

`Authority` is the only value that differs between local and Azure. The API
discovers signing keys from `/.well-known/openid-configuration` and refreshes them
automatically, so Keycloak key rotation is a non-event.

### The HTTPS-metadata guard

```csharp
if (!requireHttpsMetadata && !IsLoopback(authority))
    throw new InvalidOperationException(/* … */);
```

Fetching signing keys over plain HTTP from a remote host lets an attacker
substitute their own keys and mint valid tokens. The API refuses to start in that
configuration. Loopback is exempt because that is the local lab.

This is explicit configuration rather than `!env.IsDevelopment()`, because the
environment name is easy to lose — `dotnet run --no-launch-profile` silently makes
it Production, and the failure is an opaque 500 on every request.

## The claims transformation

The piece every Keycloak + .NET integration needs and most tutorials get wrong.
Keycloak nests roles:

```json
{
  "realm_access":    { "roles": ["platform-admin"] },
  "resource_access": { "docvault-api": { "roles": ["doc.editor"] } }
}
```

ASP.NET Core looks for flat `role` claims. Without the transformation, **every
`RequireRole` policy silently denies** and you get a 403 that looks like a
Keycloak misconfiguration.

Three details in `KeycloakClaimsTransformation.cs` worth copying:

1. **Only this client's roles are imported.** Otherwise a `doc.admin` role granted
   on some unrelated client would grant access here.
2. **It is idempotent.** `IClaimsTransformation` runs on every request; without a
   guard, role claims accumulate duplicates and grow unboundedly on long-lived
   connections.
3. **A malformed claim grants nothing.** Parsing failures log and deny — never
   fail open.

## Policies

```csharp
.AddPolicy(Policies.ReadDocuments,  p => p.RequireRole("doc.reader", "doc.editor", "doc.admin"))
.AddPolicy(Policies.Classified,     p => { p.RequireRole("doc.admin");
                                           p.AddRequirements(new StepUpAcrRequirement("silver")); })
.AddPolicy(Policies.Analytics,      p => p.RequireAssertion(/* scope contains analytics:read */))
```

Roles vs scopes: a **role** is what the *user* may do; a **scope** is what the
*client* was authorised to ask for and the user consented to. The analytics
endpoint is scope-gated because consent, not job function, is what governs it.

## Step-up: 401 with instructions

An unmet ACR requirement produces a spec-compliant challenge instead of a dead
end (RFC 9470):

```
HTTP/1.1 401 Unauthorized
WWW-Authenticate: Bearer error="insufficient_user_authentication",
                  acr_values="silver"
```

Implemented as an `IAuthorizationMiddlewareResultHandler`. Two deliberate details:
the handler leaves the requirement *unmet* rather than calling `context.Fail()`, so
"needs to step up" stays distinguishable from "will never be allowed"; and the
challenge is only issued to an already-authenticated caller, since telling an
anonymous user to step up before logging in is nonsense.

It is a **401, not a 403**, as in both of RFC 9470's examples: the authentication
event is insufficient, not the permissions. Clients must read `WWW-Authenticate`
before treating a 401 as "session expired". And it is issued only when the ACR is
the *only* unmet requirement — a `doc.reader` at bronze fails the role as well,
and offering them step-up would walk them through OTP to a plain 403.

## Tenant isolation

```csharp
var tenant = user.GetTenantContext();     // from the groups claim
store.ListForTenant(tenant.Tenant, …);
```

The tenant comes from the **token**, never from the request body — trusting a
client-supplied tenant id is how cross-tenant writes happen. Deleting a document
in another tenant returns **404, not 403**: a 403 would confirm the document
exists, leaking across the boundary.

## Machine-to-machine

`DocVault.Worker` uses `client_credentials`: no browser, no user, no long-lived
API key. The client secret is exchanged for a short-lived token on demand, so a
leaked token expires in minutes and the secret rotates centrally.

Its service account holds `doc.reader` and nothing else — least privilege applies
to machine identities exactly as to human ones. It also demonstrates the converse:
the worker belongs to no tenant group, so it gets a 403 from `/documents`. A role
alone is not an authorization.

## Other stacks

Keycloak has well-supported integrations for Spring Boot (Spring Security's
OAuth2 resource server), Node/Express, FastAPI, Django, Go and more, plus a
generic OIDC path for anything else. The concepts transfer directly — validate
issuer, audience, lifetime and algorithm; flatten `realm_access` /
`resource_access`; map to your framework's authorization primitive.

The only genuinely Keycloak-specific part is that role flattening. Everything
else is ordinary OIDC resource-server work.

---

Next: [use cases](use-cases/uc1-rbac-document-sharing.md).
