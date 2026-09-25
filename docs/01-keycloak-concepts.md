# 2. Keycloak concepts

Everything here is plain Keycloak, and applies equally to the local container,
an Azure deployment, or any other way you run it.

## Realm

A realm is an isolated universe of users, clients, roles and keys. Users in one
realm cannot log in to another, and each realm has its **own signing keys**.

This lab uses one realm, `docvault`. The `master` realm exists to administer the
server and should never hold application users.

> Tokens from a different realm are cryptographically valid but must still be
> rejected — same server, different issuer. The API tests assert exactly this
> (`Rejects_a_token_from_a_different_realm`).

## Client

Any application that talks to Keycloak. The type is decided by one question:
**can it keep a secret?**

| Type | Can keep a secret? | Used for | In this lab |
|---|---|---|---|
| `PUBLIC` | No | Browser SPAs, mobile, desktop | `docvault-web-react`, `-vue`, `-mobile`, `-desktop` |
| `CONFIDENTIAL` | Yes | Server-side apps, daemons | `docvault-worker`, `-analytics`, `docvault-api` |
| `BEARER-ONLY` | n/a | APIs that only validate tokens | *(see the note below)* |

Anything shipped to a user's device is public. A secret compiled into a mobile
app is extractable in minutes, so public clients get **no** secret and rely on
PKCE plus registered redirect URIs instead.

> **A trap worth knowing.** `docvault-api` looks like a textbook BEARER-ONLY
> client, but it uses Keycloak Authorization Services, which are *hosted by* the
> client and therefore need a service account. Keycloak rejects that combination
> with a bare HTTP 500 and no explanation. A resource server using Authorization
> Services must be `CONFIDENTIAL` with the login flows disabled — see the comment
> in `infra/terraform/20-realm/clients.tf`.

## Roles, groups and scopes

The three are constantly confused. They answer different questions:

| | Question | Example |
|---|---|---|
| **Role** | What may you *do*? | `doc.editor` |
| **Group** | Who do you *belong to*? | `/acme/engineering` |
| **Scope** | What did the *client* ask for, and did the user consent? | `analytics:read` |

**Realm roles vs client roles.** A realm role (`platform-admin`) is global. A
client role (`doc.editor` on `docvault-api`) is scoped to one application, so an
unrelated client cannot mint itself a `doc.admin` that this API would honour. The
API's claims transformation only imports roles for its own client for that reason.

**Users get groups; groups carry roles.** Onboarding an Acme engineer is then one
group membership rather than a checklist of roles. Sub-groups inherit their
parent's roles, so `/acme/engineering` gets everything `/acme` has.

> Group **attributes** *can* reach the token: the built-in User Attribute mapper
> resolves an attribute from the user's groups when the user lacks it. This realm
> sends the group **path** instead, by choice — the attribute's resolution order is
> implicit (a user attribute silently wins; across memberships, the first found
> does), while `/acme/engineering` is unambiguous and carries the department too.

## Tokens

Three tokens, three jobs — mixing them up is the most common design error:

| Token | For | Send it to an API? |
|---|---|---|
| **Access** | Authorizing API calls | **Yes** — this is the only one |
| **ID** | Telling the *client* who signed in | **No** |
| **Refresh** | Obtaining a new access token | **No** — only to Keycloak |

An access token from this lab, trimmed:

```jsonc
{
  "iss": "http://localhost:8080/realms/docvault",  // must match the API's Authority
  "aud": ["docvault-api", "account"],              // must include the API, or 401
  "sub": "6dfbeff0-…",                             // stable user id
  "exp": 1787338905,                               // 5 minutes out
  "acr": "bronze",                                 // authentication level: bronze | silver
  "scope": "openid profile email",
  "realm_access":    { "roles": ["platform-admin"] },
  "resource_access": { "docvault-api": { "roles": ["doc.editor"] } },
  "groups": ["/acme/engineering"]                  // needs an explicit mapper
}
```

Two of these exist only because something was configured:

- **`aud` contains `docvault-api`** only because of an audience mapper. Without it
  every call 401s with "The audience … is invalid".
- **`acr` is meaningful** only because the realm defines `acr.loa.map`. Without it
  `acr_values` is silently ignored and step-up appears to work while proving nothing.

Inspect any token with `make token`, or watch it live in the React app's
**TokenInspector** panel.

## Protocol mappers

A mapper puts a claim in a token. Keycloak emits very little by default — no
groups, no useful audience — so most integration problems are a missing mapper.
See `infra/terraform/20-realm/mappers.tf`.

## The flow this lab uses

Authorization Code + PKCE, for every client type:

```
browser ──▶ Keycloak  /auth?…&code_challenge=…&code_challenge_method=S256
   ▲                          │  user authenticates
   └──────────────────────────┘  redirect back with ?code=…
browser ──▶ Keycloak  /token   code + code_verifier
                               ──▶ access + id + refresh tokens
browser ──▶ your API  Authorization: Bearer <access token>
```

PKCE binds the authorization code to whoever started the flow, so an intercepted
code is useless. The **implicit flow** is not used anywhere: it returns tokens in
the URL fragment, where they land in browser history and referrer headers. It is
deprecated in OAuth 2.1.

---

Next: [3. Local development](02-local-dev.md) — the free lab.
