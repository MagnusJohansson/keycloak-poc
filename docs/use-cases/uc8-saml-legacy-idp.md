# UC8 — Moving off a legacy SAML identity provider

> Local lab and CI only. The legacy provider is a stand-in: a second realm in
> the same Keycloak, speaking SAML 2.0. It is not another vendor's product.

## Problem

Some of DocVault's people still sign in at an older identity provider that
speaks SAML. Replacing it in one night is not an option. Until it is gone,
those people should reach DocVault with the sign-in they have, and nothing in
DocVault's API or clients should have to learn SAML to let them.

## Shape

```
user ──▶ DocVault client ──▶ Keycloak (docvault realm) ──signed request──▶ legacy provider
                                  │                                          (SAML 2.0)
                                  │◀───── signed SAML response ──────────────┘
                                  │
                                  │  checks the signature, the audience, the request it answers
                                  │  maps  ACME-ENG ─▶ /acme/engineering
                                  │
                                  └── issues ITS OWN token (OIDC), as for everyone else
```

Keycloak is the broker. SAML stops at Keycloak:

| | Before | During the move | After |
|---|---|---|---|
| The person signs in at | the legacy provider | the legacy provider | Keycloak |
| The application trusts | the legacy provider | Keycloak | Keycloak |
| The application speaks | SAML | OIDC | OIDC |

The middle column is this use case. The API did not change for it, and cannot
tell heidi, who signed in through SAML, from alice, who typed a password.

## The stand-in

`infra/terraform/20-realm/saml-legacy-idp.tf` makes a realm called
`legacy-idp` with three people, two groups in its own naming, and DocVault
registered as a SAML service provider.

| Person | Legacy group | In DocVault | Shows |
|---|---|---|---|
| `heidi` | `ACME-ENG` | `/acme/engineering` | the happy path: editor in Acme |
| `ivan` | `GLOBEX` | `/globex` | tenant isolation still holds |
| `judy` | none | no group | signs in, and the API answers 403 |

They share the lab password. In the browser, choose **Legacy sign-in (SAML)**
on DocVault's login page.

A second realm of the same Keycloak is a real SAML identity provider: the
requests, responses, signatures and bindings are the standard ones. What it
cannot show is how Keycloak gets on with ADFS, Shibboleth or anything else,
and that is where real migrations spend their time.

## Configure

`infra/terraform/20-realm/saml-broker.tf`. Both files exist only when
`saml_legacy_idp_enabled` is true, which the local lab sets and the Azure
deployment does not.

```hcl
resource "keycloak_saml_identity_provider" "legacy" {
  alias     = "legacy-saml"
  entity_id = "http://localhost:8080/realms/docvault"   # who DocVault is, in SAML

  single_sign_on_service_url = ".../realms/legacy-idp/protocol/saml"
  signing_certificate        = data.keycloak_realm_keys.legacy[0].keys[0].certificate

  validate_signature         = true    # the response is checked against that certificate
  want_assertions_signed     = true    # and the assertion inside must be signed itself
  want_authn_requests_signed = true    # DocVault signs what it sends

  name_id_policy_format = "Persistent"
  principal_type        = "SUBJECT"

  sync_mode   = "IMPORT"               # per mapper instead: see the traps
  trust_email = false

  first_broker_login_flow_alias = "first-broker-login-no-link"   # see the traps
}
```

Three kinds of mapper turn an assertion into a DocVault user:

- **Username:** `${ALIAS}.${ATTRIBUTE.uid}`, so heidi is `legacy-saml.heidi`.
  The legacy system's `alice` can then never be, or collide with, DocVault's.
- **Email and names:** copied from the assertion's attributes. The address is
  not marked verified.
- **Groups:** `memberOf = ACME-ENG` puts the person in `/acme/engineering`,
  and `GLOBEX` in `/globex`. The legacy system's group names mean nothing here
  until a mapper says so, and what a DocVault group allows is still decided in
  DocVault (`roles-groups.tf`).

Each mapper says how often it runs. Groups and names are applied at every
sign-in. The username and the email address are taken at the first and not
again.

Each side checks the other with a certificate. Terraform reads each realm's
signing certificate and gives it to the other side. In a real migration those
come from the two parties' SAML metadata; DocVault's is at
`/realms/docvault/broker/legacy-saml/endpoint/descriptor`.

## Verify

```bash
make up && make seed && make api
make assert-saml        # 43 checks
```

The script plays the browser between the two sides, so it can change a SAML
response on its way to DocVault, as an attacker in that position could. It
cannot forge a signature and does not try: every bad response is a real one
from the legacy provider, changed afterwards.

- **Sign-in:** heidi, ivan and judy get the groups and roles in the table
  above; DocVault's own alice still signs in with her password.
- **On the wire:** DocVault's request is signed; the response and the assertion
  in it are both signed; the assertion names DocVault as its only audience; the
  subject is an opaque persistent identifier.
- **A changed response is refused:** with every signature removed, with the
  assertion's signature removed, saying another group or another person with
  the signatures left in place, and with a forged assertion placed in front of
  or wrapped around the signed one (signature wrapping).
- **A real response in the wrong place is refused:** heidi's earlier response
  put into judy's sign-in, the same response posted out of the blue, and a
  signed response the provider issued for a different service.
- **No takeover:** a legacy person carrying alice's email address is refused,
  nothing is linked to alice, and alice gets no email. A person DocVault already
  knows, who takes alice's address later, still signs in as herself with her
  own address. A legacy person named `alice` becomes a separate DocVault user
  with no access.
- **A mover and a leaver:** moved to another legacy group, heidi is in the new
  DocVault group at her next sign-in. Disabled in the legacy system, she cannot
  sign in.

CI runs it in the realm job, against a Keycloak that is new on every run.

The script imitates a browser. `make e2e` has one test that does the same
sign-in in a real one, through the React app; it is not run in CI.

## Traps this found

- **Keycloak's built-in first-sign-in flow offers to link accounts by email.**
  A legacy person whose email address belonged to a DocVault user was shown
  "Account already exists. Add to existing account?". Saying yes made Keycloak
  email the existing user: *"Someone wants to link your DocVault account … If
  this was you, click the link."* So anyone able to set an email address in the
  legacy system could make DocVault send alice a genuine email, and sign in as
  her if she clicked it. `trust_email = false` does not prevent this, and the
  realm's `verify_email` was off. The realm now uses a first-sign-in flow with
  no linking: a brokered person becomes a new DocVault user or is refused.
  Linking a legacy identity to an existing account is then an administrator's
  job. With the built-in flow back in place, the check sees the email arrive.
- **The sync mode decides whether a mover moves.** A mapper that runs in
  `IMPORT` mode runs once, at a person's first sign-in. With the group mappers
  left at that, heidi, moved from `ACME-ENG` to `GLOBEX` in the legacy system,
  was still an Acme editor in DocVault at her next sign-in, and nothing
  complained. They run in `FORCE` mode: at every sign-in. (The same trap as in
  [UC2](uc2-enterprise-sso-entra.md), now with a check that fails when it is
  wrong.)
- **`sync_mode = "FORCE"` on the provider re-writes the email address too, and
  can end in a 500.** The first version set `FORCE` for the whole provider. It
  then re-applied a person's email address at every sign-in, whatever the email
  mapper's own mode said. A brokered person whose address in the legacy system
  later became a DocVault user's ran into the realm's unique-email rule, and
  her next sign-in was a bare HTTP 500 from Keycloak. It failed closed: nothing
  was linked and only her own account was affected. The provider is now
  `IMPORT`, and each mapper carries its own mode.
- **`validate_signature = false` still refuses an unsigned response, and
  accepts an altered one.** With validation off and `want_assertions_signed`
  on, a response with its signatures removed was refused, which looks like
  protection. One saying a different person or group, with the old signatures
  left in place, was accepted. A test that only strips signatures would pass.
- **Two realms on one host share cookie names.** The check script first kept
  cookies by name. The legacy realm's session cookie then overwrote DocVault's,
  and every sign-in ended at DocVault with "cookie not found". A browser keeps
  them apart by path, and so does the script now. It is a property of the
  stand-in, not of SAML.

## What it does not do

- **End a running session.** DocVault learns what the legacy provider knows
  only when a person signs in. Moved to another group, heidi's running session
  keeps the old one; disabled, her session still refreshes until it ends. The
  check states both. Single logout from the legacy provider is configured and
  has not been exercised.
- **Check the outer signature when the assertion is signed.** A response with
  only its outer signature removed is accepted: the signed assertion is what
  DocVault relies on. The check states this. In the review of this change, a
  response accepted that way was still refused when its `Destination` was
  altered or removed.
- **Follow a changed email address.** It is taken at the first sign-in. A
  change in the legacy system does not reach DocVault.
- **Undo what an administrator grants in DocVault.** The group mappers manage
  the groups they map. A group given to a brokered person by hand in DocVault
  stays.
- **Test what it cannot make.** An expired assertion, a clock that is off, and
  an assertion signed with the wrong key all need a key the script does not
  have. They are not checked.
- **Encrypt assertions.** They are signed, not encrypted. Everything travels
  through the person's browser over plain HTTP on localhost.
- **Rotate certificates.** Each side has the other's certificate from
  Terraform. A key rotation on either side needs a new apply.
- **Sign-in started at the provider.** Only sign-ins that DocVault asked for
  are accepted.
- **Another vendor's provider.** See above.
- **The other direction.** A legacy application that speaks SAML, signing in
  through Keycloak, is not here yet.
- **Azure.** The Azure deployment has neither file's resources.
