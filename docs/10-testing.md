# 10. Testing and validation

```bash
make test    # 79 tests (31 API + 48 desktop-auth), no Docker, no secrets, no network
```

## The pyramid

| Layer | Tool | Asserts |
|---|---|---|
| Unit | xUnit | Claims transformation and tenant derivation in isolation |
| Integration | `WebApplicationFactory` + injected signing key | The real pipeline: policies, guards, endpoints |
| Integration (real IdP) | `ci.yml` against Docker Keycloak | The realm applies, and the silent mappers took effect |
| E2E | Playwright | Browser login → API call → step-up → logout |
| Security | xUnit | Forged and malformed tokens are rejected |

There is no test JWKS endpoint and no Testcontainers tier. The signing key is
handed to the handler as an object reference, and the "does a real Keycloak
actually emit this?" question is answered in CI rather than in the unit suite —
see [CI](#ci) below.

## Testing auth without a live IdP

`DocVaultApiFactory` runs the **real** pipeline — real `JwtBearer` handler, real
claims transformation, real policies — and swaps only the signing-key source:

```csharp
options.Authority = null!;                    // no network
options.TokenValidationParameters.IssuerSigningKey = Tokens.SigningKey;
options.TokenValidationParameters.ValidIssuer = TestTokenIssuer.DefaultIssuer;
```

Everything the tests assert on (audience, issuer, lifetime, algorithm, role
mapping, policy evaluation) is production code, so a passing test means the
production configuration works.

> `ValidIssuer` is stated explicitly rather than inherited from configuration.
> Under minimal hosting the app's own `appsettings.json` is applied *after* the
> test host's in-memory source, so relying on the override silently leaves the
> production issuer in place and every test 401s. That cost an hour; hence the
> comment in the file.

`TestTokenIssuer` mints tokens shaped exactly like Keycloak's — including
`realm_access` and `resource_access` as nested JSON — and can forge the ones a
real Keycloak never would.

## The security suite

Each test is a real attack or a real misconfiguration:

| Test | Guards against |
|---|---|
| `Rejects_an_unsigned_alg_none_token` | The canonical JWT forgery |
| `Rejects_a_token_signed_by_an_unknown_key` | Attacker-generated keypair |
| `Rejects_a_token_minted_for_a_different_audience` | Token replay across APIs |
| `Rejects_a_token_from_a_different_realm` | Multi-realm confusion |
| `Rejects_an_expired_token` | Bounded `ClockSkew` |
| `Authenticated_but_role_less_user_gets_403_not_401` | Login loops |
| `Tenants_cannot_see_each_others_documents` | Cross-tenant leakage |
| `Deleting_another_tenants_document_returns_404_not_403` | A 403 confirming existence |
| `Another_tenants_document_is_indistinguishable_from_one_that_never_existed` | Id enumeration across tenants |
| `Platform_admin_endpoint_requires_the_realm_role` | Client role ≠ realm role |
| `Analytics_requires_the_consented_scope_not_merely_a_role` | Scope vs role |
| `Stepping_up_does_not_substitute_for_the_role` | MFA ≠ permission |

The `alg:none` test is worth keeping even though the framework handles it: it
documents the expectation, and it fails loudly if someone later relaxes
`ValidAlgorithms`.

### Why 403 vs 401 is tested

A 401 tells the client "authenticate again", which sends an authenticated-but-
unauthorized user round a login loop that can never succeed. 403 says "you are
logged in, but you lack permission". Getting this wrong produces a bug report that
reads "the app logs me out at random".

## Validating the realm

Terraform is configuration, so test it as configuration:

```bash
make plan    # a clean plan on an unchanged realm should be a no-op
```

A non-empty plan against an untouched realm means something drifted — usually a
manual change in the admin console, which is exactly what infrastructure-as-code
exists to catch.

Check the pieces that fail silently:

```bash
# ACR -> LoA map took effect
curl -s http://localhost:8080/realms/docvault/.well-known/openid-configuration \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["acr_values_supported"])'

# audience mapper works
make token | grep -A3 '"aud"'
```

Both are configuration that produces **no error when missing** — the reason they
are worth an explicit check.

## CI

- `ci.yml` — build, unit, integration, security. Runs against Docker Keycloak, so
  **no cloud account and no secrets are needed**.
- `e2e.yml` — compose up, seed, Playwright.
- `deploy-azure.yml` — GitHub OIDC federation; no stored credentials.

## Not covered here

Honest gaps, listed rather than glossed over:

- **Load testing.** Token validation is cheap (signature check against a cached
  key); the login flow is not. Test the login rate you actually expect.
- **Penetration testing.** The security suite tests what I thought to test.
- **Key rotation under load.** Keycloak rotates signing keys; clients refresh JWKS
  on an unknown-`kid`. Worth exercising deliberately before you meet it in production.
