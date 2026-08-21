# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A runnable proof-of-concept lab for **[Keycloak](https://www.keycloak.org/)**, the open-source
identity and access management server — OIDC, OAuth 2.0, SAML, MFA, RBAC, federation, audit.

One correction from the original brief is load-bearing and easy to reintroduce by accident:
`docs/PRD.md` originally framed the goal as "secure data sharing and privacy" with use cases like
*encrypted messaging* and *privacy-preserving analytics*. Keycloak is an **authentication and
authorization** server — it does not encrypt application data or carry messages. Those scenarios
were re-expressed as things Keycloak genuinely does (service-to-service auth; consent + pairwise
subject identifiers). `docs/00-what-keycloak-is.md` is the canonical statement. Do not "restore"
the original framing.

> Historical note: this repo began life targeting *Skycloak*, a managed-Keycloak SaaS, before the
> intent was corrected to open-source Keycloak. The Skycloak-specific module, the vendor
> references, the old folder name and the original git remote were all removed. If you find any
> `skycloak`/`skyvault` identifier anywhere, it is a leftover and should be cleaned.
>
> There is deliberately **no git remote** configured.

## Commands

```bash
make up          # Keycloak + Postgres + Mailpit (~30s, waits for readiness)
make seed        # apply the DocVault realm via Terraform, then print secrets
make api         # .NET 10 API on :5001
make web         # React SPA on :5173      (separate shell)
make test        # 27 .NET tests - no Docker, no network, no cloud account
make e2e         # 7 Playwright tests - REQUIRES up + seed + api + web running
make plan        # realm diff; should be EMPTY on an unchanged realm
make token       # mint a real token and decode its claims
make down        # stop (keeps data)   /   make clean = also wipe volume + tfstate
```

`make help` lists everything, including the `azure-*` targets.

**Single test:**

```bash
dotnet test apps/api-dotnet/DocVault.slnx --filter "FullyQualifiedName~Rejects_an_expired_token"
cd tests/e2e-playwright && npx playwright test --grep "tenant isolation"
cd apps/mobile-flutter && flutter test
```

The solution is `DocVault.slnx` (new format) — there is no `.sln`.

**Terraform is not on PATH on this machine.** The Makefile assumes it is. Either install it or run
the binary directly; every module needs `terraform init -backend=false` before `validate`.

## Architecture

### The central idea

`infra/terraform/20-realm` takes a `keycloak_url` variable and **nothing else changes** between the
local Docker Keycloak and the Azure deployment. It talks only to the Admin REST API, which is
identical in both. Two consequences the repo depends on:

- the free local lab is a *faithful rehearsal*, not a simplified toy;
- console drift is a test failure — `make plan` should be empty.

CI applies the module to a real Keycloak on every run. Preserve this: anything that makes the
module environment-specific defeats the point.

### Terraform modules

| Module | Provider | Scope |
|---|---|---|
| `10-keycloak-azure` | `azurerm` | Keycloak itself on Azure: Container Apps + private Postgres + Key Vault |
| `20-realm` | `keycloak/keycloak` | **the realm — source of truth** |
| `30-azure` | `azurerm` | your apps: Container Apps, Static Web Apps, Sentinel |

Apply order for cloud: `10-keycloak-azure` → `20-realm` → `30-azure`.

`infra/local/realm-export/docvault-realm.json` is a **generated artifact** (`make export-realm`).
Never hand-edit it — the next `make seed` overwrites it. Terraform is the source of truth.

### Demo app: DocVault

Multi-tenant document workspace. Tenants are groups (`/acme`, `/globex`), roles ride on groups, and
the API derives tenant from the **group path** — Keycloak has no mapper for group *attributes*, so
the path is what travels.

Demo users each exist to make one outcome visible: `alice` (editor, happy path), `bob` (other
tenant, isolation), `carol` (admin + MFA step-up), `dave` (no roles → **403, not 401**).
Password `DocVaultLab!2026`.

### Client matrix

`apps/web-react` is the full reference (PKCE, RBAC guards, step-up, TokenInspector). Vue, Flutter,
React Native and Electron are runnable-lite — each documents the *one* thing that differs from the
web case. The web clients use `react-oidc-context` + `oidc-client-ts` rather than `keycloak-js`,
deliberately: plain OIDC keeps the client code portable.

## Traps this repo has already hit

Every one of these cost real debugging time and is now load-bearing. Do not "simplify" them away.

**Silent configuration** — these produce *no error* when missing, only a downstream 401/403:

- **Audience mapper.** Without it `aud` is the calling client and every request 401s with
  "The audience … is invalid".
- **`acr.loa.map` on the realm.** Without it `acr_values` is silently ignored and step-up appears
  to work while proving nothing. Verify via `acr_values_supported` in the discovery document.
- **`KeycloakClaimsTransformation`.** Keycloak nests roles in `realm_access`/`resource_access`;
  ASP.NET Core wants flat role claims. Without it every `RequireRole` silently denies. Must stay
  idempotent (runs per request) and import only *this* client's roles.

**Terraform / realm:**

- A resource server using Authorization Services **cannot** be `BEARER-ONLY` — Keycloak returns a
  bare HTTP 500. `docvault-api` is `CONFIDENTIAL` with login flows disabled.
- Authentication executions are ordered by **creation sequence**; Terraform parallelises, so
  `authn-stepup.tf` needs explicit `depends_on` chains or the flow comes out shuffled.
- `*_scopes` resources take the client's internal UUID (`.id`), not the OAuth `client_id` string.
- `keycloak_openid_client_default_scopes` is **authoritative** and its built-in list varies by
  version — avoided deliberately in favour of per-client mappers, to keep the module portable.
- Keycloak generates `pairwiseSubAlgorithmSalt` itself → perpetual drift without `ignore_changes`.
  Never hardcode that salt.
- Keycloak *fetches* `sectorIdentifierUri` at mapper-creation time (chicken-and-egg with the API).

**Keycloak on Azure (`10-keycloak-azure`):**

- Postgres HA is **unavailable on the Burstable tier**; a `precondition` catches the bad
  combination at plan time instead of at apply.
- `KC_HOSTNAME` + `KC_HTTP_ENABLED` + `KC_PROXY_HEADERS=xforwarded` are all required behind
  Container Apps' TLS termination, or Keycloak builds redirect/issuer URLs from the internal
  hostname and every OIDC client breaks.
- The `KC_HOSTNAME` chicken-and-egg is solved via `azurerm_container_app_environment.default_domain`
  (computed on the *environment*, which exists before the app) — do not reintroduce a two-pass apply.
- `jdbc-ping` (Keycloak 26.1+ default) is what makes Container Apps viable — no multicast. It also
  makes the database a hard dependency for clustering, not just storage.
- DB password alphabet is restricted: `@ : / ? &` break the JDBC URL and surface as *auth* errors.
- No `0.0.0.0` Postgres firewall rule — that admits every Azure tenant. VNet + delegated subnets.
- `start`, never `start-dev`: dev mode disables hostname and HTTPS checks and will run happily in
  production while silently weakening both.

**Runtime:**

- **`WWW-Authenticate` is not CORS-safelisted.** Without `.WithExposedHeaders("WWW-Authenticate")`
  the browser cannot read the RFC 9470 step-up challenge, so the SPA sees a bare 403 and the user
  has no recovery path.
- `MapInboundClaims = false` on JwtBearer, or .NET rewrites claim names and hides `realm_access`.
- `RequireHttpsMetadata` is explicit config, not `!IsDevelopment()` — the environment name is easy
  to lose (`--no-launch-profile` silently means Production). A startup guard rejects the unsafe
  combination for non-loopback authorities.
- React: redirect off `/callback` via `<Navigate>`; `history.replaceState` does not notify React
  Router, which parks the user on "Completing sign in…" forever.
- Docker Hub rate-limits anonymous pulls (429). Image refs default to quay.io and a GCR mirror,
  overridable via `POSTGRES_IMAGE` / `KEYCLOAK_IMAGE` / `MAILPIT_IMAGE`.

## Conventions

- **Both the local lab and CI must work with no cloud account and no secrets.** `make test` and
  `ci.yml` run entirely against Docker Keycloak. Keep it that way.
- Keep the Keycloak version pinned in step across `infra/local/docker-compose.yml` and
  `10-keycloak-azure`'s `keycloak_version`.
- `infra/environments/local/realm.tfvars` **is committed** (admin/admin against a throwaway
  container; `make seed` and CI depend on it). All other `.tfvars` are gitignored.
- 403 vs 401 is a tested behaviour, not an implementation detail: 401 sends an
  authenticated-but-unauthorized user round a login loop that cannot succeed.
- Cross-tenant reads return **404, not 403** — a 403 confirms the resource exists.
- Comments explain *why*, especially for the traps above. Several exist solely to stop a future
  reader "fixing" something into breakage.

## Verification status

Proven against real software: 68 Terraform resources applied to Keycloak 26.6.3 with a clean
re-plan; 27 .NET tests (including forged `alg:none`, wrong-key, wrong-audience, wrong-realm and
expired tokens); 7 Playwright tests in a real browser; React/Vue build; Flutter analyzes clean.

**Not executed:** `10-keycloak-azure` and `30-azure` have never been applied — that needs an Azure
subscription. `10-keycloak-azure` got as far as a read-only `terraform plan` succeeding against
real Azure APIs (20 resources), which validates SKUs, subnet delegations and private DNS, but
nothing is runtime-proven. uc2 (Entra SSO) is documented from real schemas but unrun. OTP
*enrolment* is not automated; the e2e test asserts the challenge is issued, which is the part that
regresses silently.
