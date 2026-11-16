# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A runnable proof-of-concept lab for **[Keycloak](https://www.keycloak.org/)**, the open-source
identity and access management server — OIDC, OAuth 2.0, SAML, MFA, RBAC, federation, audit.

> This repo briefly targeted *Skycloak*, a managed-Keycloak SaaS, before being refocused on
> open-source Keycloak. The rename is complete — no `skycloak`/`skyvault` identifiers remain
> outside this note. Treat any that reappear as a mistake.

## Commands

```bash
make up          # Keycloak + Postgres + Mailpit (~30s, waits for readiness)
make seed        # apply the DocVault realm via Terraform, then print secrets
make api         # .NET 10 API on :5001
make web         # React SPA on :5173      (separate shell)
make mcp         # MCP server on :5002  (separate shell; needs up + seed + api)
make assert-mcp  # 24 checks of MCP authorization - REQUIRES up + seed + api + mcp running
make test        # 151 .NET tests (32 API + 64 MCP + 55 desktop-auth) - no Docker, network or cloud
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
cd apps/mobile-flutter && flutter test --dart-define-from-file=config/local.json
```

There are **two** solutions, both `.slnx` (new format) — there is no `.sln`:
`apps/api-dotnet/DocVault.slnx` and `apps/desktop-winui/DocVault.Desktop.slnx`. See the WinUI
traps for why they must stay separate.

Terraform is on PATH (v1.15.8, Homebrew). Every module needs `terraform init -backend=false`
before `validate`.

**Mobile:** `make android-reverse` before running the Flutter app on Android — see the issuer trap.

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
the API derives tenant from the **group path**. That is a *choice*, not a Keycloak limitation: the
built-in User Attribute mapper does resolve an attribute from the user's groups (verified on
26.6.3). The path wins because that resolution is implicit — a user attribute silently beats the
group's, and across memberships the first found wins — and because `/acme/engineering` carries
tenant *and* unit. Do not reintroduce "group attributes can't reach a token"; it is false.

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
- Authentication executions are ordered by **`priority`, and only by priority**. Unset, every
  execution is 0 and ties come back in database order: not creation order, and different on each
  fresh Keycloak. `authn-stepup.tf` sets `priority` on every execution and subflow; the
  `depends_on` chains it once relied on do NOT pin the order. When the OTP subflow sorted first,
  a correct password got 400 "Invalid username or password" (2 of 9 CI runs). CI asserts the order.
- `*_scopes` resources take the client's internal UUID (`.id`), not the OAuth `client_id` string.
- `keycloak_openid_client_default_scopes` is **authoritative** and its built-in list varies by
  version — avoided deliberately in favour of per-client mappers, to keep the module portable.
- **Authorization permissions must set `type = "scope"`.** Unset, the provider creates a *resource*
  permission, which ignores `scopes` and applies to every scope of the resource; with the
  resource server's `UNANIMOUS` strategy an editor was refused even `view`. Silent, because the API
  never calls the model — `make assert-authz` (also in CI) is the only thing that would notice.
  Keycloak also **ignores a `type` change on an existing permission** (apply reports success, the
  next plan repeats it), and `replace_triggered_by` on a new `terraform_data` does not fire. The
  fix therefore uses new addresses (`*_scope`) *and* new names (`*-scope-permission`), so plain
  `make seed` repairs an old realm; reusing a name could collide, since old and new run in parallel.
- Keycloak generates `pairwiseSubAlgorithmSalt` itself → perpetual drift without `ignore_changes`.
  Never hardcode that salt.
- Keycloak *fetches* `sectorIdentifierUri` at mapper-creation time (chicken-and-egg with the API).
- SPA origins in `20-realm` are **lists** (`web_react_origins`, `web_vue_origins`) so a realm can
  trust the deployed site AND localhost. `api_origin` is deliberately **singular**: the analytics
  client carries a pairwise `sub`, and Keycloak rejects such a client with redirect URIs spanning
  multiple hosts unless a Sector Identifier URI is set. Do not "make it consistent".

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
- **`terraform destroy` does not remove a resource group that holds anything Terraform did not
  create**, and Application Insights adds a "Failure Anomalies" smart detector rule by itself. The
  destroy then exits *successfully* while leaving the group and its bill behind. Both Azure modules
  set `prevent_deletion_if_contains_resources = false`; safe only because each creates the group it
  deletes. Do not copy that into a module targeting a pre-existing group.
- **`make azure-destroy` must tear down all three layers** — realm, then `30-azure`, then
  `10-keycloak-azure` — in that order. It once destroyed only Keycloak, silently leaving
  `rg-docvault-lab` running. The realm goes first because it lives *inside* Keycloak: destroy the
  server first and `20-realm`'s azure workspace is left holding state for objects that no longer
  exist.

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

**MCP server (`apps/mcp-dotnet`, `20-realm/mcp.tf`)** - see `docs/13-integration-mcp.md`:

- **The MCP server's Keycloak `client_id` is its URL** (`var.mcp_resource_url`), on purpose. With
  resource indicators the token's `aud` is that URL, and standard token exchange only lets a
  client exchange a token whose `aud` contains its own `client_id`. A short name signs users in
  fine and then fails every tool call with `403 "Client is not within the token audience"`.
- **`resource-indicators` is an EXPERIMENTAL Keycloak feature**, enabled in
  `infra/local/docker-compose.yml` only. Off, nothing errors: `resource` is ignored and `aud`
  silently comes from the audience mapper. `10-keycloak-azure` does not enable it, so MCP is a
  local-lab + CI feature; do not claim it works on Azure.
- **`IgnoreTrailingSlashWhenValidatingAudience = false`** in the MCP server. .NET's default
  accepts `…/mcp/` for `…/mcp`. Found by a unit test.
- **The SDK (ModelContextProtocol.AspNetCore 2.4) does not send MCP's `insufficient_scope`
  challenge** and by default derives the metadata `resource` from the Host header.
  `ScopeChallengeMiddleware` and the fixed, ABSOLUTE `ResourceMetadataUri` exist for those (a
  relative one still builds the 401's `resource_metadata` link from Host); do not replace
  them with `[Authorize]` on tools, which hides tools and returns an error clients cannot act on.
- **`mcp_server` has `full_scope_allowed = false` plus an API audience mapper.** Without the
  first, an administrator's realm roles ride into the exchanged token; without the second, a
  user with no roles fails at the exchange (`Requested audience not available`) instead of
  getting the API's 403.
- **`WebApplicationFactory` + settings read at startup:** the MCP tests use `UseSetting`, because
  `Program` reads `McpSettings` while building, before `ConfigureAppConfiguration` values exist.
- **The scope middleware fails closed** (400 on a body it cannot parse, path compared without a
  trailing slash) and each tool re-checks its scope. Do not turn the 400 into a pass-through.
- **`AudienceValidator` requires exactly one audience.** .NET's default accepts any match in an
  `aud` array.
- `tools/assert-mcp-authz.py` must exit non-zero on every failure path, and its dave check asserts
  the API's refusal text: without `mcp_server_api_audience` dave fails at the exchange instead,
  which is also an error. It handles cookies by hand: `http.cookiejar` drops Keycloak's cookies
  on plain-HTTP localhost. It must never print a token.

**Desktop clients (`apps/desktop-winui`, `apps/desktop-electron`):**

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
- Both desktop clients configure from a **file** (`config.json` for Electron,
  `appsettings.json` for WinUI) with `DOCVAULT_*` env overrides. They cannot share the React
  app's `.env`: Vite only exposes `VITE_*`, Electron never reads `.env`, and they use different
  Keycloak clients.
- Electron's `config.json` is **gitignored**; ship `config.json.example`. It was committed with a
  live deployment's hostnames once and had to be purged from history before the repo went public.
  A missing `config.json` is fine — the app falls back to the local lab.
- Desktop logging (`DesktopLogging`) writes to file + Debug + Console, and OidcClient's own
  diagnostics are routed into it, so the log contains the full authorize URL.

**Mobile clients (`apps/mobile-flutter`, `apps/mobile-react-native`):**

- **Do NOT set `android:taskAffinity=""` on MainActivity.** An empty affinity means "no affinity to
  *any* task", **not** "shared affinity" — so each activity starts its own task. Flutter's template
  ships it; with it, AppAuth's `RedirectUriReceiverActivity` and the `singleTask`
  `AuthorizationManagementActivity` land in separate tasks, the latter is recreated rather than
  resumed, and sign-in hangs forever with only `W/AppAuth: No stored state` in logcat.
  The fix is to **remove** the attribute so all three share the default package affinity.
  Confirmed with `dumpsys activity activities`: tasks #58/#59/#60 before, one task after.
  (An earlier fix here set `""` on AppAuth's activities *too*, which by the same logic guaranteed
  the split — the manifest comment records this so it is not reintroduced.)
- **`flutter_appauth` rejects plain HTTP itself**, before Android's cleartext policy or iOS ATS is
  ever consulted: `java.lang.IllegalArgumentException: only https connections are permitted`. The
  network-security-config and `NSExceptionDomains` entries are necessary but **not sufficient**.
  `allowInsecureConnections` is derived from whether the issuer is `http` — never hardcode it true.
- **Each mobile app has its own client AND its own URI scheme** (`docvault-flutter` /
  `io.docvault.flutter://`, `docvault-reactnative` / `io.docvault.rn://`). A custom scheme is
  claimed OS-wide, so two apps sharing one is ambiguous — Android resolves it
  non-deterministically, iOS favours the last installed — and a redirect can reach the wrong app.
  Do not re-merge them.
- Flutter config is **compile-time** (`String.fromEnvironment` resolves at build), so changing a
  value needs a rebuild, not a hot restart. One `config/local.json` serves simulator *and* emulator
  — see the issuer trap for why there is no `local-ios.json`.

**Runtime:**

- **`WWW-Authenticate` is not CORS-safelisted.** Without `.WithExposedHeaders("WWW-Authenticate")`
  the browser cannot read the RFC 9470 step-up challenge, so the SPA sees a bare 401 and the user
  has no recovery path.
- **The step-up challenge is a 401, not a 403** (RFC 9470 §3): the authentication event is
  insufficient, not the permissions. Every client reads `WWW-Authenticate` *before* treating a 401
  as "session expired". It is issued **only when the ACR is the sole unmet requirement** — a
  `doc.reader` at bronze gets a plain 403, since stepping up could not help.
- `MapInboundClaims = false` on JwtBearer, or .NET rewrites claim names and hides `realm_access`.
- `RequireHttpsMetadata` is explicit config, not `!IsDevelopment()` — the environment name is easy
  to lose (`--no-launch-profile` silently means Production). A startup guard rejects the unsafe
  combination for non-loopback authorities.
- React: redirect off `/callback` via `<Navigate>`; `history.replaceState` does not notify React
  Router, which parks the user on "Completing sign in…" forever.
- **`oidcConfig.ts` throws during module evaluation**, before `createRoot().render()`, so React
  never mounts and an `<ErrorBoundary>` cannot help — the page is simply blank and the reason
  reaches only the console. `index.html` carries a classic-script `error`/`unhandledrejection`
  listener that paints the message into `#root`. It is deliberately scoped to **boot**
  (`root.childElementCount > 0` → bail): a later silent-renew rejection must not wipe a running
  app. Styles there are inline because `index.css` is imported by `main.tsx` and is never
  reached on that path.
- Docker Hub rate-limits anonymous pulls (429). Image refs default to quay.io and a GCR mirror,
  overridable via `POSTGRES_IMAGE` / `KEYCLOAK_IMAGE` / `MAILPIT_IMAGE`.
- **Vite reads `.env` only at startup**, and the dev server uses `strictPort`. A stale process
  therefore keeps serving the old values and makes a config change look like it did nothing —
  kill and restart rather than assuming the edit was wrong. This has burned time twice.
- **The issuer is derived from the request host.** `KC_HOSTNAME` is unset in the lab, so the same
  realm mints `iss: http://10.0.2.2:8080/...` when reached at `10.0.2.2` and `http://localhost:8080/...`
  when reached at `localhost`. An API trusts exactly one. Two consequences:
  - Android must use `make android-reverse` (forwards the device's own localhost to the host)
    rather than the `10.0.2.2` alias. `config/local.json` is shared with iOS for this reason;
    there is no `local-ios.json`.
  - A client's authority and its API base URL **must move to Azure together**. Half-migrating is
    the single easiest mistake here, because sign-in still succeeds — Keycloak does not know
    which API you will call — so it only surfaces as a bare 401 at the first request.

  Both fail with `IDX10205`. Fix by matching the issuer, never by adding valid issuers.

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
- **Diagrams are Mermaid in the Markdown**, not exported images: GitHub renders them natively,
  they diff, and they follow the reader's light/dark theme. A picture that must be re-exported by
  hand drifts exactly like the resource counts did. Validate before committing — invalid syntax
  renders as an error box on GitHub:
  `npx -y @mermaid-js/mermaid-cli@11 -i d.mmd -o d.svg`
- **The repo is GPL-3.0-or-later**, and every hand-written source file opens with a four-line
  SPDX header (`Copyright (C) 2026 Magnus Johansson` + `SPDX-License-Identifier: GPL-3.0-or-later`).
  `tools/license-header.sh` applies it and `--check` verifies it; the `license` job in `ci.yml`
  fails a build whose new file is missing one. Scope is defined by the include/exclude lists at
  the top of that script — JSON is excluded because it has no comment syntax, so those projects
  carry `"license"` in `package.json` instead. Three insertion rules are load-bearing: the header
  goes *after* a shebang, *after* `<!doctype html>`, and *after* an `<?xml?>` declaration, since
  all three must remain the first bytes of their file. The script's body is wrapped in a `main()`
  function on purpose — it rewrites itself, and prepending lines to a script bash is still reading
  shifts its file offset and makes it execute garbage.

## Releasing

Publishing a GitHub Release (tag `vX.Y.Z` on `main`) runs `.github/workflows/release.yml`: it
checks the tag is on main and that `CHANGELOG.md` has a `## X.Y.Z` section, runs the .NET tests,
then attaches a `git archive` source tarball, `SHA256SUMS.txt` and the Sigstore attestation bundle
(`.sigstore.json`). Add the changelog section BEFORE publishing; the workflow runs from the tagged
commit, so a tag on a commit without this workflow attaches nothing and leaves an unsigned release.

## Verification status

Proven against real software: 86 Terraform resources applied to the local Keycloak 26.6.3 with a
clean re-plan (Azure was applied with the 74 that predate the MCP server); 32 API + 64 MCP +
55 desktop-auth .NET tests (including forged `alg:none`, wrong-key,
wrong-audience, wrong-realm and expired tokens, and 404-not-403 across the tenant
boundary); 7 Playwright tests in a real browser; 12 Flutter tests; React/Vue build;
Flutter analyzes clean.

**`10-keycloak-azure` has been applied for real** (Azure, swedencentral) and works: Keycloak came
up on Container Apps behind HTTPS, `20-realm` applied all 74 resources to it, discovery returns the
public FQDN as `iss` (so `KC_HOSTNAME` + `KC_PROXY_HEADERS` are right), `acr_values_supported`
includes `silver`, and a `client_credentials` token carries `aud: docvault-api` plus the
service-account role. Both environments re-plan clean.

**`30-azure` has also been applied for real.** The API runs on Container Apps, pulling from an
ACR via managed identity (AcrPull, no registry password), and accepts tokens issued by the Azure
Keycloak. Both Azure modules and the realm re-plan clean.

**The MCP server has been verified against the local lab only**: `make assert-mcp` passes 24
checks with real sign-ins as alice, bob, carol and dave (audience = the server URL, unknown
resource refused, exchange to the API, no realm roles for carol, scope challenge, tenant
isolation). It has also been driven from Claude Code by the user: sign-in, consent, list
documents, then `create_document` with only the read scope, which produced Claude Code's
"needs additional permissions (it asked for the scope docvault:write)" prompt, a re-authorization
with `scope=docvault:read … docvault:write`, `prompt=consent` and the `resource` parameter, and a
retried call that created the document.

**Not verified:** the MCP server on Azure (feature flag not enabled there), and MCP clients other
than Claude Code. Also not verified: the Flutter client against *Azure* (only the local lab was driven on the
emulator), and the iOS simulator path at all. React Native ships an auth module, not a runnable app.

**Not executed:** the Static Web Apps are provisioned but empty — Terraform does not upload SPA
content, so no browser client has been served from Azure. uc2 (Entra SSO) is documented from real schemas but unrun. OTP
*enrolment* is not automated; the e2e test asserts the challenge is issued, which is the part that
regresses silently.

The **WinUI 3 client has been run end-to-end on Windows against the Azure deployment**: sign-in
through the system browser, a token with `iss` = the Azure issuer, `azp` = `docvault-winui`,
`aud` = `docvault-api`, `resource_access` roles intact, and a tenant-scoped document list returned
by the API. It still cannot be compiled or run on this machine (macOS); the `windows-latest` CI
job compiles it, and the user verified the runtime behaviour.

**The Flutter client has been run end-to-end on an Android emulator against the local lab** —
driven through `adb` (`input tap`/`input text`, `screencap`, `dumpsys activity activities`) rather
than assumed. Signed in as `alice`, token carried `iss = http://localhost:8080/realms/docvault`,
`aud: [docvault-api, account]`, `azp = docvault-flutter`, roles intact, documents rendered, and
**zero** JWT validation failures in the API log. This is what caught both Android traps above; each
earlier attempt to fix them by reasoning from the manifest alone was wrong.

**React has been verified against the full Azure stack** (Azure Keycloak *and* Azure API) with an
ad-hoc Playwright run asserting the `/documents` call returns 200 from `ca-docvault-api` and never
touches `localhost:5001` — the assertion matters, because a passing "documents loaded" check alone
does not prove *which* API served them.

**The Electron client has also been run end-to-end against Azure** — `azp = docvault-desktop`,
`groups = ["/acme/engineering"]`, tenant-scoped documents returned. Both desktop clients are
therefore exercised, with distinct `azp` values confirming they are genuinely separate Keycloak
clients rather than sharing one.
