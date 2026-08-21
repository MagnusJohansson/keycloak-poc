# 13. Conclusion and next steps

## What this lab showed

**Keycloak is a complete IAM server, and the work is in configuring it, not
choosing it.** OIDC, SAML, MFA, RBAC, fine-grained authorization, federation and
audit are all there and all free. What you spend is operational effort and the
time to model your realm properly.

**The realm belongs in version control.** One Terraform module,
`infra/terraform/20-realm`, provisions a local Docker Keycloak and an Azure
deployment with no change but a URL. That is what makes the free local lab a
faithful rehearsal rather than a simplified toy — and it means "someone clicked
something in the admin console" shows up as drift in `make plan` instead of as a
mystery in production.

**Most integration failures are silent configuration.** The audience mapper, the
ACR→LoA map, the claims transformation — each produces no error when missing,
just a 401 or 403 that looks like a bug somewhere else. The habit worth taking
away is `make token`: read the token before debugging the code.

## Things that surprised me building it

Worth recording, because none are in the obvious tutorials:

- A resource server using Authorization Services **cannot** be `BEARER-ONLY`.
  Keycloak returns a bare 500 with no explanation.
- `keycloak_openid_client_default_scopes` is **authoritative** — it silently
  replaces every built-in scope, and the built-in list differs by version.
  Avoiding it is what keeps the realm module portable.
- Authentication executions are ordered by **creation sequence**, so Terraform's
  parallelism shuffles the flow unless you chain `depends_on`.
- Keycloak **fetches** `sectorIdentifierUri` while creating a mapper, which makes
  the realm depend on the API that depends on the realm.
- `WWW-Authenticate` is not CORS-safelisted, so a step-up challenge is invisible
  to a cross-origin SPA until you explicitly expose the header.
- Since 26.1 the default cache stack is `jdbc-ping`: cluster discovery goes
  through the database, not multicast. That is what makes Keycloak viable on
  Azure Container Apps — and it makes the database a hard dependency for
  clustering, not just for storage.

## Where to go next

**Harden the browser story.** The SPA keeps tokens in `sessionStorage`, which no
amount of care makes XSS-proof. A backend-for-frontend moves them server-side
behind an `HttpOnly` cookie. That is the single biggest security upgrade
available here.

**Replace OTP with passkeys.** Swap `auth-otp-form` for `webauthn-authenticator`.
The ACR contract, the API policy and the client code are unchanged — which is the
payoff for modelling authentication *levels* rather than mechanisms.

**Write an event listener SPI.** Scraping `jboss-logging` output works; a real
SPI posting structured events to Event Hub or Log Analytics is the production
answer, and SPIs are the main extension point worth learning.

**Add user federation.** LDAP or Active Directory federation is a large part of
why organisations pick Keycloak, and this lab does not exercise it.

**Make the audit trail actionable.** Collecting logs nobody reads is theatre.
Start with three alerts: `LOGIN_ERROR` spikes, any admin event touching role
mappings, and `REMOVE_TOTP`.

**Practise an upgrade.** Bump the pinned version locally, run `make seed` and the
test suite, and see what breaks — while it is cheap.

## If you are planning to run Keycloak for real

Questions worth answering with your own numbers:

1. **Who owns it at 3am?** Authentication downtime is total downtime. If there is
   no answer, that is the finding.
2. **Have you restored a backup?** Not configured one — restored it.
3. **What is your upgrade cadence?** Keycloak moves quickly; falling behind turns
   routine upgrades into migrations.
4. **Is the realm reproducible?** If the answer involves the admin console, a
   rebuild after an incident is guesswork.
5. **Does self-hosting beat managed for you?** Compare engineer-hours, not
   licence fees. Managed Keycloak vendors and Red Hat build of Keycloak are
   legitimate answers, and the realm export is your path in either direction.

## Reference

| | |
|---|---|
| Keycloak documentation | https://www.keycloak.org/documentation |
| Server administration guide | https://www.keycloak.org/docs/latest/server_admin/ |
| Securing applications guide | https://www.keycloak.org/docs/latest/securing_apps/ |
| Terraform provider | `keycloak/keycloak` |
| RFC 8252 | OAuth 2.0 for Native Apps |
| RFC 9470 | OAuth 2.0 Step Up Authentication Challenge |
| RFC 7636 | PKCE |
