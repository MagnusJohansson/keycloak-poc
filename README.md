# DocVault — a Keycloak identity lab

A runnable proof of concept for [Keycloak](https://www.keycloak.org/), the
open-source identity and access management server. It covers running Keycloak,
configuring a realm as code, deploying to Azure, and integrating it with web,
mobile, desktop and backend clients.

**The whole lab runs locally, for free.** The realm is defined once in Terraform
and applied unchanged to either a local Docker Keycloak or an Azure deployment —
so the free local lab is a faithful rehearsal for the cloud one, not a
simplified toy.

## Quickstart

```bash
cp .env.example .env
make up      # Keycloak + Postgres + Mailpit  (~30s)
make seed    # apply the DocVault realm via Terraform
make api     # .NET 10 API on :5001
make web     # React SPA on :5173   (in another shell)
```

Sign in as **alice** / `DocVaultLab!2026`. `make help` lists everything.

The Keycloak admin console is at <http://localhost:8080/admin> (`admin`/`admin`) —
note it opens on the `master` realm, so switch to **`docvault`** to see the demo
users and clients.

Prerequisites: Docker, .NET 10 SDK, Node 20+, Terraform 1.9+.

## What it demonstrates

| # | Scenario | Where |
|---|---|---|
| 1 | RBAC + per-document ownership via Keycloak Authorization Services | [uc1](docs/use-cases/uc1-rbac-document-sharing.md) |
| 2 | Enterprise SSO — Entra ID federated as an external OIDC IdP | [uc2](docs/use-cases/uc2-enterprise-sso-entra.md) |
| 3 | Step-up MFA — `acr_values`, RFC 9470 challenge, conditional OTP | [uc3](docs/use-cases/uc3-step-up-mfa.md) |
| 4 | Machine-to-machine — `client_credentials`, service accounts | [uc4](docs/use-cases/uc4-service-to-service.md) |
| 5 | Consent + data minimisation — pairwise subject identifiers | [uc5](docs/use-cases/uc5-consent-and-privacy.md) |
| 6 | Audit → SIEM → Azure Sentinel | [uc6](docs/use-cases/uc6-audit-siem-sentinel.md) |

## Repository map

```
docs/                     the guide - start at 00-what-keycloak-is.md
infra/
  terraform/10-keycloak-azure/  Keycloak on Azure       (azurerm)
  terraform/20-realm/           realm config            (keycloak/keycloak)  <- source of truth
  terraform/30-azure/           your apps on Azure      (azurerm)
  local/                        docker-compose lab
apps/
  api-dotnet/             .NET 10 resource server + worker + tests
  web-react/              full reference client (PKCE, RBAC, step-up, TokenInspector)
  web-vue/                runnable-lite
  mobile-flutter/         runnable-lite  (flutter_appauth + Keychain/Keystore)
  mobile-react-native/    auth module    (react-native-app-auth)
  desktop-electron/       runnable-lite  (loopback PKCE, tokens never in the renderer)
  desktop-winui/          WinUI 3 / .NET 10 (Duende OidcClient, DPAPI) + cross-platform auth lib
tests/e2e-playwright/     browser login -> API call
tools/                    export-realm.sh, decode-token.sh
```

## The idea worth stealing

The realm lives in Terraform, not in the admin console. One module targets any
Keycloak:

```bash
make seed         # local Docker Keycloak
make seed-azure   # Keycloak on Azure
```

Same 68 resources, same realm; only a URL and a credential differ. Two
consequences worth having:

- **The local lab is faithful.** If it stops being so, `make seed` fails
  immediately instead of during a migration.
- **Console drift is a test failure.** `make plan` should be empty; when someone
  changes something by hand, CI says so.

CI applies the module to a real Keycloak on every run, so the claim is checked
rather than asserted.

## Verified

Built and checked against real software, not just written down:

- 68 Terraform resources applied to Keycloak 26.6.3, with a clean re-plan;
  `acr_values_supported`, the audience mapper and service-account roles confirmed
  in a real token.
- .NET 10 API: 27 tests pass, including forged `alg:none`, wrong-key,
  wrong-audience, wrong-realm and expired tokens, and 403-vs-401 for an
  authenticated-but-unauthorized caller.
- Desktop auth library: 30 tests pass on macOS/Linux. The WinUI XAML shell is
  compiled by the `windows-latest` CI job; it has not been run end-to-end.
- 7 Playwright tests pass in a real browser against real Keycloak.
- React and Vue build; Flutter analyzes clean and its tests pass.
- The Azure module plans cleanly against real Azure APIs (20 resources), but has
  not been applied.

## Licence

MIT — see [LICENSE](LICENSE).
