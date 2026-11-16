# Changelog

Each release attaches a source archive with a signed build provenance
attestation; see [Releases](README.md#releases). The release workflow refuses a
tag that is not on `main`.

## 1.0.1

- The MCP client's consent screen no longer lists "User roles". Its token never
  carried roles; the built-in `roles` scope was only attached, and so offered.
  `make assert-mcp` now checks the screen (25 checks).
- The step-up flow's Terraform no longer carries the old note saying
  `depends_on` pins the order of a login flow. It does not; `priority` does.
- README: a TL;DR at the top, a note on how the lab was built and reviewed, and
  corrected counts (87 Terraform resources, 57 desktop auth tests).

## 1.0.0

The first tagged release of the lab. Everything here was built between August
and November 2026 and is described in the [guide](docs/README.md).

### The lab

- **Keycloak 26.6 as code.** One Terraform module (`infra/terraform/20-realm`,
  86 resources) defines the realm and applies unchanged to the local Docker
  Keycloak and to Keycloak on Azure Container Apps. CI applies it to a real
  Keycloak on every run and fails on drift.
- **A .NET 10 API** as resource server: role-based access, tenant isolation from
  the group path, RFC 9470 step-up for classified documents, and a background
  worker using `client_credentials`.
- **Six clients:** React and Vue (PKCE in the browser), Flutter and React Native,
  Electron and WinUI 3.
- **Six use cases**, from RBAC to an audit trail in Azure Sentinel.

### An MCP server authorized by Keycloak

- `apps/mcp-dotnet`: an AI assistant as a client of the realm, following the MCP
  authorization specification of 2026-07-28. Audience-bound tokens (RFC 8707),
  protected resource metadata (RFC 9728), consented scopes with MCP's
  `insufficient_scope` challenge, and token exchange (RFC 8693) to the API
  instead of token passthrough. See [docs/13-integration-mcp.md](docs/13-integration-mcp.md).
- Local lab and CI only: it needs Keycloak's experimental `resource-indicators`
  feature, which the Azure module does not enable.

### Fixed along the way

- The step-up login flow had no execution priorities, so Keycloak ordered its
  steps differently on each fresh instance and could refuse a correct password.
  Every step now has an explicit priority, and CI asserts the order.

### Verified

- 153 .NET tests (API, MCP server, desktop auth library), including forged
  `alg:none`, wrong-key, wrong-audience, wrong-realm and expired tokens.
- `make assert-mcp`: 24 checks against a real Keycloak, signing in as the demo users.
- 7 Playwright tests in a real browser; 12 Flutter tests.
- What has and has not been run against Azure is listed in the
  [README](README.md#verified).
