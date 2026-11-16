# Security policy

DocVault is a **lab**: a proof of concept for learning and demonstrating Keycloak.
It is not a product, has no releases to support, and should not be run as it is
with real users or real data. Its security posture is still the point of it, so
reports are welcome.

## Reporting a vulnerability

Please **don't open a public issue** for a vulnerability. Report it privately instead:

1. Go to the repository's [Security tab](https://github.com/MagnusJohansson/keycloak-poc/security).
2. Click **Report a vulnerability**.

Please include:
- which part it concerns (the realm in `infra/terraform/20-realm`, the API, the MCP server, a client);
- the request, token or configuration that triggers it;
- what happened, and what you expected instead.

This project is maintained by one person in their spare time, so responses are
best effort. Fixes land on `main`, and a GitHub Security Advisory credits the
reporter unless you'd rather not be named.

## What counts as a vulnerability

Anything that makes the lab teach something unsafe, or that breaks a check it
claims to make. For example:
- a token the API or the MCP server accepts and should refuse (wrong audience,
  issuer, signature, lifetime or scope);
- a way to read or change another tenant's documents;
- a way for an MCP client to act beyond the scopes the user consented to, or
  beyond the user's own roles;
- a realm setting in Terraform that grants more than its comment says.

## Known limitations

These are deliberate for a lab and are not vulnerabilities in themselves:
- **Published demo credentials.** The demo users share a documented password, and
  the local Keycloak runs with `admin`/`admin` in development mode over plain HTTP.
  All of it is for a throwaway container on loopback.
- **An in-memory document store.** Nothing is persisted or encrypted at rest.
- **The MCP server relies on an experimental Keycloak feature**
  (`resource-indicators`), and is verified against the local lab only. See
  [docs/13-integration-mcp.md](docs/13-integration-mcp.md).
- **No rate limiting** on the API or the MCP server.
- **MFA enrolment is not automated** in the end-to-end tests.
