// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

import { WebStorageStateStore } from 'oidc-client-ts';
import type { AuthProviderProps } from 'react-oidc-context';

const authority = import.meta.env.VITE_OIDC_AUTHORITY;
const clientId = import.meta.env.VITE_OIDC_CLIENT_ID;

if (!authority || !clientId) {
  // Fail loudly at startup rather than producing a confusing redirect loop later.
  throw new Error('VITE_OIDC_AUTHORITY and VITE_OIDC_CLIENT_ID must be set. Copy .env.example to .env.');
}

export const oidcConfig: AuthProviderProps = {
  // The realm issuer. oidc-client-ts appends /.well-known/openid-configuration and
  // discovers every endpoint from there, so this single value is all that changes
  // when moving between the local Keycloak and an Azure deployment.
  authority,
  client_id: clientId,

  redirect_uri: `${window.location.origin}/callback`,
  post_logout_redirect_uri: window.location.origin,

  // Authorization code flow. oidc-client-ts applies PKCE automatically and there is
  // no way to disable it - which is exactly right for a browser app. The implicit
  // flow ("id_token token") is deliberately not used: it puts tokens in the URL
  // fragment, where they land in browser history and referrer headers.
  response_type: 'code',

  scope: 'openid profile email',

  // Renew the access token in the background before it expires, using a hidden
  // iframe against the Keycloak session cookie. Access tokens live 5 minutes
  // (see 20-realm/realm.tf), so without this the user is interrupted constantly.
  automaticSilentRenew: true,
  silent_redirect_uri: `${window.location.origin}/silent-renew.html`,

  // sessionStorage, NOT localStorage. localStorage persists across browser restarts
  // and is readable by any script on the origin, so an XSS flaw yields a long-lived
  // token. sessionStorage at least dies with the tab.
  //
  // The genuinely secure option for a production app is a backend-for-frontend that
  // keeps tokens server-side in an HttpOnly cookie. This lab uses a pure SPA because
  // that is what the PRD asks to demonstrate; docs/05-integration-web.md explains
  // the trade-off.
  userStore: new WebStorageStateStore({ store: window.sessionStorage }),

  // Strip ?code=&state= from the URL so a refresh does not try to redeem an
  // already-used authorization code.
  //
  // This only tidies the address bar - it deliberately does NOT navigate, because
  // history.replaceState does not notify React Router. The actual redirect off
  // /callback is a <Navigate> on that route in App.tsx.
  onSigninCallback: (user) => {
    const returnTo = (user?.state as { returnTo?: string } | undefined)?.returnTo ?? '/';
    window.history.replaceState({}, document.title, returnTo);
  },
};

export const apiBaseUrl: string = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5001';
