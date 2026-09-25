# 5. Web integration (React & Vue)

Source: `apps/web-react` (full reference) and `apps/web-vue` (runnable-lite).

## Library choice

This lab uses **`react-oidc-context` + `oidc-client-ts`** rather than Keycloak's
own `keycloak-js` adapter.

That is a deliberate choice. `oidc-client-ts` speaks plain OIDC, so the client
code contains nothing Keycloak-specific and would work unchanged against any
compliant provider. `keycloak-js` is perfectly good and slightly more convenient
for Keycloak-only shops — but it couples your frontend to one vendor's adapter,
and it has historically lagged on browser storage-partitioning changes. Given
this repo's whole theme is portability, the standards-based library fits better.

Vue uses `oidc-client-ts` directly in a composable. Comparing the two files is
instructive — the framework glue differs, the protocol configuration is identical,
because that part is a property of OIDC rather than of the framework.

## Configuration

```ts
export const oidcConfig: AuthProviderProps = {
  authority: import.meta.env.VITE_OIDC_AUTHORITY,   // the ONLY value that changes per environment
  client_id: 'docvault-web-react',
  redirect_uri: `${window.location.origin}/callback`,
  response_type: 'code',                            // PKCE is automatic and cannot be disabled
  scope: 'openid profile email',
  automaticSilentRenew: true,
  silent_redirect_uri: `${window.location.origin}/silent-renew.html`,
  userStore: new WebStorageStateStore({ store: window.sessionStorage }),
};
```

Moving to the Azure deployment is one line:

```diff
- VITE_OIDC_AUTHORITY=http://localhost:8080/realms/docvault
+ VITE_OIDC_AUTHORITY=https://<your-keycloak-host>/realms/docvault
```

## Where tokens are stored

`sessionStorage`, not `localStorage`. `localStorage` survives browser restarts and
is readable by any script on the origin, so one XSS flaw yields a long-lived
token. `sessionStorage` at least dies with the tab.

**Neither is genuinely safe.** Any token reachable from JavaScript is reachable
from injected JavaScript. The robust answer is a **backend-for-frontend**: the
server holds the tokens, the browser gets an `HttpOnly`, `SameSite=Strict` session
cookie, and the SPA never sees a bearer token at all.

This lab uses a pure SPA because that is what the PRD asked to demonstrate, and
because seeing the token is what makes the model teachable. For a production app
handling anything sensitive, use a BFF.

## Silent renewal

Access tokens live five minutes. `automaticSilentRenew` refreshes them in a hidden
iframe against the Keycloak session cookie, so the user is not interrupted.

`silent-renew.html` is a real Vite entry point rather than a CDN script, so it
works offline and under a strict CSP. Renewal is owned **only** by the OIDC
library — the fetch wrapper deliberately does not also retry on 401. Two competing
refresh attempts would trip the realm's refresh-token reuse detection and kill the
session.

> If third-party cookies are blocked (Safari ITP, Chrome's phase-out), silent
> renewal in an iframe fails once the SPA and Keycloak are on different sites. A
> custom domain that shares a parent site with your app avoids it; a BFF sidesteps
> it entirely.

## Guards are usability, not security

```tsx
<RequireRole anyOf={['doc.editor', 'doc.admin']}>
  <NewDocumentForm />
</RequireRole>
```

Anyone can edit the JavaScript running in their own browser. The API enforces the
real boundary — `RequireRole` only spares the user a pointless 403. The lab proves
it: `Reader_cannot_create_a_document` posts directly with a `doc.reader` token and
still gets 403.

`RequireRole` renders an explanation rather than redirecting. Sending an
authenticated-but-unauthorized user to a login page is the browser equivalent of
answering 403 with 401 — a loop that logging in again cannot break.

## Step-up

```
fetch /documents/classified
  → 401 + WWW-Authenticate: Bearer error="insufficient_user_authentication", acr_values="silver"
  → signinRedirect({ extraQueryParams: { acr_values: 'silver' }, prompt: 'login' })
  → new token with acr=silver
  → retry
```

`prompt: 'login'` matters: without it, the existing SSO cookie satisfies the
request silently and the user never sees a challenge — which defeats the point of
asserting presence. Full walkthrough in [uc3](use-cases/uc3-step-up-mfa.md).

## CORS

The SPA and API are on different origins, so every authenticated request is
preflighted. The API allows the SPA origins and the `Authorization` header, and
deliberately does **not** enable `AllowCredentials`: tokens travel in a header,
not a cookie, so credentialed CORS would widen the attack surface for nothing.

`web_origins` must also be set on the Keycloak client, or the token endpoint call
from the browser fails.

> **One non-obvious requirement.** The API must expose `WWW-Authenticate`:
>
> ```csharp
> .WithExposedHeaders("WWW-Authenticate")
> ```
>
> It is not one of the CORS-safelisted response headers, so without this a
> cross-origin SPA cannot read it. The step-up challenge then becomes invisible:
> the API correctly returns 401 with `acr_values="silver"`, the browser sees only
> a bare 401, and the user has no way to recover. The Playwright suite caught
> exactly this.

## TokenInspector

`apps/web-react/src/components/TokenInspector.tsx` renders the decoded access
token beside the UI reacting to it. Almost every "why am I getting a 403?" is
answered by looking at `aud`, `realm_access`, `resource_access` and `acr`
together — and noticing which is missing.

A production app would not ship this.

## Running

```bash
make web       # React on :5173
make web-vue   # Vue on :5174
```

Both use `strictPort`, so a port shift fails loudly instead of producing
`Invalid parameter: redirect_uri`.

---

Next: [6. Mobile integration](05-integration-mobile.md).
