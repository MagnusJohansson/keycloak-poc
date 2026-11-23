# Changelog

Each release attaches a source archive with a signed build provenance
attestation; see [Releases](README.md#releases). The release workflow refuses a
tag that is not on `main`.

## 1.5.0

- **Sharing a document, decided by OpenFGA.** A document can be private to its
  owner, who shares it with a person or a department of the same tenant, and
  revokes it again. The API asks OpenFGA about every document before showing
  it. See [UC1](docs/use-cases/uc1-rbac-document-sharing.md) and
  [chapter 14](docs/14-fine-grained-authorization.md).
- Keycloak stays the only place membership lives. OpenFGA stores who owns a
  document and who it is shared with; the caller's tenant and departments go
  with each check, built from the token. A person moved out of a department in
  the directory loses its shares with their next token.
- A share records the person's Keycloak user id, never their username, which
  could be renamed or reused. The API looks people up as a new client,
  `docvault-api-directory`, with `view-users` only. Someone in another tenant
  gets the same 404 as nobody.
- Fails closed: if OpenFGA cannot answer, the API answers 503, never 404, 403,
  an empty list or "allow". An administrator cannot delete a private document
  they cannot see. Owners may delete their own documents.
- An AI assistant can share and unshare, behind a new scope of its own,
  `docvault:share`, with its own line on the consent screen. The audit log
  records who each share went to.
- The React app offers private documents, a sharing panel, and Delete on your
  own documents.
- `Sharing:Mode` is required. Azure runs no OpenFGA: `30-azure` sets
  `TenantOnly`, where a private document is refused.
- Measured on the way, and written up: OpenFGA's list-objects drops results
  past its limit with a 200; its check cache delays a new share and a revoke
  alike; it believes contextual tuples completely; Terraform reports a replaced
  model destroyed while OpenFGA keeps it.
- New: `make test-model` (the model's 30 checks on OpenFGA's own engine),
  `make assert-fga` (41 checks against the real lab), one more browser test
  (11). 294 .NET tests. 130 Terraform resources in `20-realm` and 2 in
  `25-openfga` locally.
- `make clean` now removes the realm's local Terraform state. It has lived in a
  workspace since before 1.0.0, and `make clean` went on removing the old file.
- Local lab and CI only.

## 1.4.0

- **A legacy application that only speaks SAML.** `apps/saml-reports` signs its
  people in through Keycloak, which is the SAML identity provider here: the
  other direction from 1.3.0. The same people reach it as reach the API,
  including those from the LDAP directory and the legacy SAML provider. See
  [UC9](docs/use-cases/uc9-saml-application.md).
- The application checks what its SAML library does not: where a response was
  addressed, whether this browser asked for it, and whether it has been used
  before, each decided on the signed assertion and not on the response's
  unsigned envelope. 48 tests forge responses with a key the tests hold; `make
  assert-saml-app` (30 checks) adds the real Keycloak.
- Its SAML subject is the username. A persistent identifier locked out people
  from the read-only directory, and Keycloak reported it as a wrong password.
- One more browser test (10). 238 .NET tests. 127 Terraform resources locally.
- Local lab and CI only. The application is a stand-in written for the lab.

## 1.3.0

- **A legacy SAML identity provider, brokered.** People who still sign in at an
  older SAML provider reach DocVault through Keycloak, which checks the signed
  assertion, maps the provider's groups onto the realm's, and issues its own
  token. The API does not change. `make assert-saml` (43 checks) plays the
  browser between the two and hands DocVault responses that were stripped,
  altered, wrapped, replayed or issued for another service. See
  [UC8](docs/use-cases/uc8-saml-legacy-idp.md).
- A first sign-in through the provider can no longer link to an existing
  account. Keycloak's built-in flow offered that by email address, and emailed
  the existing user a confirmation link.
- Two more browser tests (9): a person from the LDAP directory and one from
  the legacy SAML provider sign in through the React app.
- Local lab and CI only. The legacy provider is a stand-in, a second Keycloak
  realm. 121 Terraform resources locally.

## 1.2.0

- **People from an LDAP directory.** The local lab gains an OpenLDAP container
  and the realm a read-only LDAP user federation over TLS. The directory says
  who someone is and where they belong; the realm's groups still say what that
  allows. `make assert-ldap` (53 checks) signs in as directory people and then
  changes the directory: a mover, a leaver, an outage, a person taking
  someone else's name or address. See
  [UC7](docs/use-cases/uc7-ldap-directory.md).
- Keycloak's cache is off for directory people. With the default, a person
  removed from a group, or deleted, kept getting tokens.
- The demo users are put in their groups one user at a time. The earlier
  per-group resource owned the whole member list and could not coexist with a
  directory. An existing realm upgrades with `make seed`.
- Only named directory groups count, each in one place in the realm's group
  tree, and a username and email address are not re-read at each sign-in: a
  directory cannot create groups in the realm, or lend a person someone else's
  address.
- Local lab and CI only, and OpenLDAP only. 95 Terraform resources locally.

## 1.1.0

- **An audit log for the MCP server.** One JSON record per tool call, scope
  challenge and refused token: the user, the client the assistant signed in
  through, the tool, and how it ended. To the log, and to a JSON Lines file when
  `Audit:File` is set. Records never hold a token, a refused token's claims or a
  rejected title.
- **The API logs each authenticated request with its client (`azp`) and trace
  id,** so a request an assistant made for a user can be told from one the user
  made, and matched to the MCP server's record of it.
- The realm keeps Keycloak's `TOKEN_EXCHANGE` events: the MCP server swapping a
  user's token for an API token.
- `make assert-mcp` reads both logs and Keycloak's events after its own sign-ins
  (34 checks). 190 .NET tests.

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
