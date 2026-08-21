# 9½. Use cases

Six scenarios, each with the problem, the realm configuration, the application
code, and — most importantly — **how to verify it actually works**.

| # | Scenario | PRD origin |
|---|---|---|
| [1](use-cases/uc1-rbac-document-sharing.md) | **RBAC + per-document authorization** — tenant isolation, roles on groups, Keycloak Authorization Services | §4 "secure file sharing" — direct match |
| [2](use-cases/uc2-enterprise-sso-entra.md) | **Enterprise SSO** — Entra ID federated as an upstream OIDC IdP, group→role mapping | added; the Azure-native identity story |
| [3](use-cases/uc3-step-up-mfa.md) | **Step-up MFA** — `acr_values`, ACR→LoA map, conditional OTP, RFC 9470 challenge | added |
| [4](use-cases/uc4-service-to-service.md) | **Machine-to-machine** — `client_credentials`, service accounts, no standing API key | §4 "encrypted messaging", re-framed |
| [5](use-cases/uc5-consent-and-privacy.md) | **Consent + data minimisation** — optional scopes, pairwise subject identifiers | §4 "privacy-preserving analytics", re-framed |
| [6](use-cases/uc6-audit-siem-sentinel.md) | **Audit → SIEM** — Keycloak events into Azure Sentinel, with queries | added |

Two of the PRD's original scenarios describe a product category Keycloak is not
in; [00-what-keycloak-is](00-what-keycloak-is.md) explains the re-framing and why
the replacements are honest rather than merely convenient.
