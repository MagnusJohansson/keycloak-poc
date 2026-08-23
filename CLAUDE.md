# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A runnable proof-of-concept lab for **[Keycloak](https://www.keycloak.org/)**, the open-source
identity and access management server — OIDC, OAuth 2.0, SAML, MFA, RBAC, federation, audit.

> This repo briefly targeted *Skycloak*, a managed-Keycloak SaaS, before being refocused on
> open-source Keycloak. Every vendor reference was removed, so any `skycloak`/`skyvault`
> identifier you find is a leftover and should be cleaned.

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
| `30-azure` | `azurerm` | your apps: Container Apps, Static Web Apps, Log Analytics (Sentinel opt-in) |

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
React Native, Electron and WinUI 3 are runnable-lite — each documents the *one* thing that differs
from the web case. The web clients use `react-oidc-context` + `oidc-client-ts` rather than `keycloak-js`,
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

**One module, two environments (`20-realm`):**

- `20-realm` is applied to BOTH the local Docker Keycloak and Azure, so every target selects a
  **Terraform workspace** first (`local` / `azure`). Without that they share one
  `terraform.tfstate`: seeding Azure silently overwrites the local realm's state, leaving the local
  realm untracked and the cloud realm impossible to destroy. This actually happened.
- Anything reading `terraform output` (`show-secrets`, `azure-secrets`) is workspace-sensitive too.
- Never add a target that touches `$(REALM_DIR)` without a `$(WS_LOCAL)` or `$(WS_AZURE)` first.

**Deploying to Azure — found by actually applying it:**

- The API's startup guard refuses a non-loopback authority over plain HTTP, so `30-azure` MUST set
  `Keycloak__RequireHttpsMetadata=true`. `appsettings.json` ships `false` for the loopback default;
  without the override the container exits with `ActivationFailed`.
- A VNet-integrated Container Apps environment makes Azure create a third, platform-managed
  resource group (`ME_<env>_<rg>_<region>`) holding its load balancer and public IP. Expected, not
  drift — `managedBy` points at the environment, and it is deleted with it. Do not try to manage
  or remove it.
- **Region is `TF_VAR_location`**, defaulting to `swedencentral`. It must be set before the first
  apply: Azure regions are immutable on nearly every resource here, so changing it forces a full
  destroy/recreate including the database. `static_web_app_location` is deliberately separate
  because Static Web Apps exist in only five regions.
- **Sentinel is opt-in** (`enable_sentinel`, default false) because it bills per GB analysed.
  Collection works without it — Container Apps streams Keycloak stdout to Log Analytics either
  way — so do not re-enable it by default "to make uc6 work".
- **Static Web Apps exist in only 5 regions** (`centralus`, `eastus2`, `westus2`, `westeurope`,
  `eastasia`). Hence `static_web_app_location`, separate from `location`.
- Azure server-side-adds three things Terraform would otherwise delete every plan:
  `service_endpoints` on the Postgres-delegated subnet, and the `Consumption` workload profile on
  both container apps and their environments. All are `ignore_changes`.
- The container app is `count`-guarded on `container_image` so the registry can be created and
  populated first. `-target` does **not** solve this — Terraform still requires every variable
  when targeting.

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

**WinUI 3 desktop (`apps/desktop-winui`):**

- Configuration is `DocVault.WinUI/appsettings.json` (copied next to the exe), overridable with
  `DOCVAULT_`-prefixed environment variables. The file is primary because a GUI app launched from
  the Start menu has nowhere to pick up env vars. Loading and validation live in
  `DesktopSettings` in the **library**, so they are tested without Windows.
- `Uri.TryCreate(x, UriKind.Absolute)` returns true for `localhost:8080/...`, parsing `localhost`
  as the scheme. `DesktopSettings.Validate` therefore also checks the scheme is http/https — do
  not "simplify" that away.

- The OIDC logic is a plain `net10.0` library (`DocVault.Desktop.Auth`) *on purpose* — WinUI XAML
  compiles only on Windows, so keeping the logic out of the shell is what makes it testable on
  Linux/macOS and in the normal CI job. Do not move logic into the XAML code-behind.
- `DocVault.Desktop.slnx` must keep its `<Configurations>` platform mappings. WinUI cannot be
  Any CPU (the Windows App SDK ships native binaries), so the solution maps Any CPU/x64/x86/ARM64
  onto the project's concrete platforms. Without them Visual Studio errors with "specifies a
  project configuration ... that does not exist for that project". Any CPU maps to x64 on purpose:
  x64 runs on ARM64 under emulation, ARM64 does not run on x64.
- `DocVault.WinUI` must stay **out of `apps/api-dotnet/DocVault.slnx`** — that solution builds on
  ubuntu in CI and a Windows-only TFM breaks it. It lives in `apps/desktop-winui/DocVault.Desktop.slnx`.
- `OidcClient` refuses plain-HTTP discovery by default; the local lab is HTTP on loopback, and the
  error names the *policy* rather than the URL. `DiscoveryPolicy.RequireHttps` is derived from
  whether the authority is loopback — never hardcode it false.
- `LoginRequest.FrontChannelExtraParameters` is the real API for `acr_values`/`prompt` (not a
  `FrontChannel.Extra` property, which does not exist in 7.x).
- Unpackaged (`WindowsPackageType=None`), so `PasswordVault` is unavailable — tokens use DPAPI.

- **Keycloak wildcards only work at the END of a redirect URI.** `http://127.0.0.1:*/callback` is
  matched literally and every authorization request fails with `invalid_request`. Both desktop
  clients register `http://127.0.0.1/*`, which works because Keycloak ignores the port for a
  loopback host (RFC 8252). Verified empirically against a real Keycloak — do not "tidy" it back.
- Desktop logging (`DesktopLogging`) writes to file + Debug + Console, and OidcClient's own
  diagnostics are routed into it, so the log contains the full authorize URL.

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

Proven against real software: 71 Terraform resources applied to Keycloak 26.6.3 with a clean
re-plan; 27 API + 30 desktop-auth .NET tests (including forged `alg:none`, wrong-key,
wrong-audience, wrong-realm and expired tokens); 7 Playwright tests in a real browser; React/Vue
build; Flutter analyzes clean.

**`10-keycloak-azure` has been applied for real** (Azure, swedencentral) and works: Keycloak came
up on Container Apps behind HTTPS, `20-realm` applied all 71 resources to it, discovery returns the
public FQDN as `iss` (so `KC_HOSTNAME` + `KC_PROXY_HEADERS` are right), `acr_values_supported`
includes `silver`, and a `client_credentials` token carries `aud: docvault-api` plus the
service-account role. Both environments re-plan clean.

**`30-azure` has also been applied for real.** The API runs on Container Apps, pulling from an
ACR via managed identity (AcrPull, no registry password), and accepts tokens issued by the Azure
Keycloak. Both Azure modules and the realm re-plan clean.

**Not executed:** the Static Web Apps are provisioned but empty — Terraform does not upload SPA
content, so no browser client has been served from Azure. uc2 (Entra SSO) is documented from real schemas but unrun. OTP
*enrolment* is not automated; the e2e test asserts the challenge is issued, which is the part that
regresses silently.

The **WinUI 3 client has been run end-to-end on Windows against the Azure deployment**: sign-in
through the system browser, a token with `iss` = the Azure issuer, `azp` = `docvault-winui`,
`aud` = `docvault-api`, `resource_access` roles intact, and a tenant-scoped document list returned
by the API. It still cannot be compiled or run on this machine (macOS); the `windows-latest` CI
job compiles it, and the user verified the runtime behaviour.
