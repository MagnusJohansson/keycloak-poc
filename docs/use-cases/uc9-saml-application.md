# UC9 — A legacy application that only speaks SAML

> Local lab and CI only. The application is a stand-in written for this lab,
> not a real legacy product.

## Problem

[UC8](uc8-saml-legacy-idp.md) moved the people: they can come from an old SAML
identity provider and still reach DocVault. This is the other half. Some
applications cannot be taught OIDC: a vendor product, or something nobody
dares to rebuild. They speak SAML and nothing else. They should keep working
while everything around them moves to Keycloak, and they should get their
people from the same place as everyone else.

## Shape

```
user ──▶ DocVault Reports (SAML only) ──signed request──▶ Keycloak (docvault realm)
              ▲                                              │  signs the person in:
              │                                              │  password, the LDAP directory,
              └────────── signed SAML response ──────────────┘  or the legacy SAML provider
```

Keycloak is the identity provider here, and the application is the service
provider: the reverse of UC8. One realm now speaks OIDC to the new clients and
SAML to the old application, and the same people sign in to both.

| | UC8 | UC9 |
|---|---|---|
| Keycloak is | the service provider | the identity provider |
| The old thing is | where people sign in | an application |
| Keycloak must refuse | a bad response | (it issues them) |
| A bad response must be refused by | Keycloak | **the application** |

The last row is what this use case is about. In UC8 the checks were Keycloak's
to make. Here they are the application's, and a SAML library does only some
of them.

## The application

`apps/saml-reports`: a small ASP.NET Core application, "DocVault Reports". It
has no login page, never sees a JWT, and never calls the DocVault API. It
shows who the assertion says you are, and a report for your tenant.

| Path | |
|---|---|
| `/` | a page with your username and tenant, and a sign-out button |
| `/me` | what the assertion said, as JSON |
| `/reports/summary` | the tenant's report; 403 for a person in no tenant |
| `/saml/login`, `/saml/acs`, `/saml/logout` | the SAML endpoints |

It uses the ITfoxtec.Identity.Saml2 library. It signs its requests with a key
of its own and accepts responses signed by one pinned certificate, the
realm's.

## Configure

`infra/terraform/20-realm/saml-reports.tf` registers the application in the
realm and makes its key pair:

```hcl
resource "keycloak_saml_client" "reports" {
  client_id = "http://localhost:5003/saml"      # the application's SAML name

  sign_documents  = true                        # the response is signed
  sign_assertions = true                        # and the assertion inside it

  client_signature_required   = true            # only requests the application signed
  signing_certificate         = <the application's certificate>
  valid_redirect_uris         = ["http://localhost:5003/saml/acs"]
  assertion_consumer_post_url = "http://localhost:5003/saml/acs"

  name_id_format     = "username"               # see the traps
  full_scope_allowed = false                    # no roles in the assertion
}
```

Three mappers decide what the application learns: the username, the email
address, and the groups by full path. The application takes the tenant from
the first segment of the group path, as the API does.

`make reports` starts the application with the realm's certificate and its own
key pair, read from Terraform's outputs.

## What the application checks

A signed response proves who signed it. It does not prove that it was meant
for this application, that this browser asked for it, or that it is new.

| Check | Done by | Refused as |
|---|---|---|
| Signed by the pinned certificate, and unchanged since | the library | `invalid` |
| Issued by this realm | the library | `invalid` |
| Its audience is this application | the library | `invalid` |
| Not expired, and not from the future | the library | `invalid` |
| Addressed to this application's endpoint | **the application** | `destination` |
| Answers a request this browser made | **the application** | `unsolicited`, `in_response_to` |
| Names exactly one person | **the application** | `attributes` |
| Not used before, and the request not answered before | **the application** | `replay` |

The ones in bold are in `SamlController.cs` and `PendingSignIn.cs`. With one
removed, the application's tests fail, and nothing else would have noticed:
the sign-in works.

**Each of them is decided on the signed assertion, not on the response around
it.** A SAML response says where it is going and what it answers twice: on its
envelope, and inside the assertion (`SubjectConfirmationData`). The library
accepts a response whose envelope is not signed, as long as the assertion is.
So the envelope's copy is whatever the sender wrote, and only the assertion's
copy can be believed. The application checks both and trusts the signed one.

To tie a response to a browser, the application puts the id of its request in
a cookie, encrypted so that the browser cannot write its own, and accepts only
a response whose signed assertion answers that id. Each request is answered
once and each assertion accepted once; it remembers both until the assertion
would have expired.

## Verify

```bash
make test                # 47 of the tests are this application's
make up && make seed
make reports             # the application on :5003 (separate shell)
make assert-saml-app     # 30 checks against the real Keycloak
```

**The application's tests** (`DocVault.Reports.Tests`) have an identity
provider of their own with a signing key. So they can make what a script
between two real parties cannot: a response that is correctly signed and still
wrong. Expired, not valid yet, for another application, from another issuer,
addressed elsewhere, answering someone else's request, readdressed on its
unsigned envelope, naming two people, or used twice. Plus the ones with bad
signatures: none, another key, changed after signing, and a forged assertion
beside the signed one.

Eighteen deliberate breaks of the application's checks were tried one at a
time. Seventeen failed a test. The eighteenth is the list of used assertion
ids, which cannot be reached without first getting past the once-per-request
check; it is kept so that replay does not rest on one line.

**`make assert-saml-app`** adds the real Keycloak:

- alice, bob and dave sign in and get what their groups allow.
- erin, from the LDAP directory, and heidi, from the legacy SAML provider,
  reach the application too. For heidi that is SAML in and SAML out, with
  Keycloak in between.
- Keycloak refuses a request the application did not sign.
- Real responses from Keycloak are refused when they arrive changed, in
  another browser, readdressed on their envelope to another sign-in, unasked,
  twice, or when Keycloak issued them for a different application.
- Sign-out works only from the application's own page.

`make e2e` has one test that signs in to the application in a real browser.

## Traps this found

- **The library does not check everything.** It verified the signature, the
  issuer, the audience and the validity period. It did not check where the
  response was addressed, whether anyone had asked for it, or whether it had
  been used before. A response taken from alice's sign-in could be posted from
  another browser and was accepted, with a perfectly good signature. Those
  three checks are the application's own.
- **The first version checked the request id on the unsigned envelope.** It
  compared the browser's request with the `InResponseTo` on the response
  element. With the outer signature removed, which the library accepts, that
  attribute is the sender's to write. An independent review took a real,
  unused response for alice from Keycloak, removed the outer signature, set the
  envelope's `InResponseTo` to a sign-in of the reviewer's own, and was signed
  in as alice. Every other check passed: the assertion's signature was intact.
  The copy of the request id inside the assertion still named alice's sign-in,
  and nothing compared the two. The application now decides on the signed
  copy, for the request id and for the recipient. A test and an end-to-end
  check replay the attack.
- **Clearing the sign-in cookie too early let another site cancel a sign-in.**
  The cookie was deleted as soon as anything was posted to the application's
  SAML endpoint, before the post was checked. That endpoint has to accept
  posts from another site, so any page could post rubbish to it and cancel a
  sign-in in progress. The cookie is now cleared only when a sign-in succeeds.
- **A persistent NameID locks out people from a read-only directory.** The
  first version asked for an opaque, persistent subject, which is the better
  choice. Keycloak keeps that identifier as an attribute on the user. It could
  not write one to erin, who comes from the read-only LDAP directory, and her
  sign-in to this application failed. Keycloak's event said
  `invalid_user_credentials`; only the server log said `Federated storage is
  not writable`. Local users and brokered users were fine, so a test with
  alice alone would have passed. The subject is now the username.
- **The sign-in cookie has to cross sites.** Keycloak's response comes back as
  a POST from another site, and a browser sends only `SameSite=None` cookies
  with that. `None` requires `Secure`. It works in the lab because Chromium
  accepts `Secure` cookies on `http://localhost`. It has not been tried in
  another browser.
- **The SAML library had three releases in a week.** Two of them were security
  fixes to response processing. The lab pins the newest, which is younger than
  the seven days Dependabot is told to wait here.

## What it does not do

- **Single logout.** Signing out ends the application's session only. The
  person is still signed in at Keycloak, and the next visit signs them in
  without a password. The check states this.
- **Remember used requests and assertions across a restart, or across
  instances.** The lists are in memory. A second instance, or a restart within
  an assertion's few minutes of life, would accept a replay.
- **Protect a response in transit.** A response is a credential until it is
  used or expires. Someone who takes one that has not been delivered, and the
  encrypted sign-in cookie of the browser it was meant for, can use it once.
  Over HTTPS that needs the person's own machine; on plain HTTP it needs only
  the network, which is why the application refuses plain HTTP off the
  loopback.
- **Follow a change while a session lasts.** The application learns who you
  are at sign-in. A change of group reaches it at the next one.
- **Survive a key rotation.** The realm's certificate is pinned from
  Terraform's output. When the realm's key changes, the application needs the
  new one. It does not read Keycloak's metadata.
- **Encrypt assertions.** They are signed, and travel through the browser over
  plain HTTP on localhost. Off the loopback the application refuses to start
  without HTTPS.
- **Roles.** The application gets groups and derives a tenant. There is no
  role in the assertion.
- **A real legacy product.** The application was written for this lab, with a
  current library. A real one brings its own SAML stack and its own quirks.
- **Azure.** The Azure deployment does not run it.
