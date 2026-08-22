# 9. Use cases

Six scenarios, each with the problem, the realm configuration, the application
code, and — most importantly — **how to verify it actually works**.

| # | Scenario |
|---|---|
| [1](use-cases/uc1-rbac-document-sharing.md) | **RBAC + per-document authorization** — tenant isolation, roles on groups, Keycloak Authorization Services |
| [2](use-cases/uc2-enterprise-sso-entra.md) | **Enterprise SSO** — Entra ID federated as an upstream OIDC IdP, group→role mapping |
| [3](use-cases/uc3-step-up-mfa.md) | **Step-up MFA** — `acr_values`, ACR→LoA map, conditional OTP, RFC 9470 challenge |
| [4](use-cases/uc4-service-to-service.md) | **Machine-to-machine** — `client_credentials`, service accounts, no standing API key |
| [5](use-cases/uc5-consent-and-privacy.md) | **Consent + data minimisation** — optional scopes, pairwise subject identifiers |
| [6](use-cases/uc6-audit-siem-sentinel.md) | **Audit → SIEM** — Keycloak events into Azure Sentinel, with queries |

Each builds on the realm defined in [03-realm-design](03-realm-design.md), so they
are best read after it.
