// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

import { UserManager, WebStorageStateStore, type User } from 'oidc-client-ts';
import { ref, readonly } from 'vue';

/**
 * The same OIDC configuration as the React app, expressed as a Vue composable.
 *
 * Worth comparing the two side by side: the framework integration differs, the
 * protocol configuration does not. `authority`, `response_type: 'code'` and PKCE
 * are identical because they are properties of OIDC, not of React or Vue.
 */
const userManager = new UserManager({
  authority: import.meta.env.VITE_OIDC_AUTHORITY,
  client_id: import.meta.env.VITE_OIDC_CLIENT_ID,
  redirect_uri: `${window.location.origin}/callback`,
  post_logout_redirect_uri: window.location.origin,
  response_type: 'code',
  scope: 'openid profile email',
  automaticSilentRenew: true,
  userStore: new WebStorageStateStore({ store: window.sessionStorage }),
});

const user = ref<User | null>(null);
const isLoading = ref(true);
const error = ref<string | null>(null);

async function initialise() {
  try {
    // Returning from Keycloak: the URL carries ?code=&state=.
    if (window.location.search.includes('code=')) {
      user.value = await userManager.signinCallback() ?? null;
      // Strip the code from the URL so a refresh does not try to redeem it twice.
      window.history.replaceState({}, document.title, '/');
    } else {
      user.value = await userManager.getUser();
    }
  } catch (e) {
    error.value = String(e);
  } finally {
    isLoading.value = false;
  }
}

void initialise();

userManager.events.addUserLoaded((u) => {
  user.value = u;
});
userManager.events.addUserUnloaded(() => {
  user.value = null;
});

export function useAuth() {
  return {
    user: readonly(user),
    isLoading: readonly(isLoading),
    error: readonly(error),
    signIn: () => userManager.signinRedirect(),
    signOut: () => userManager.signoutRedirect(),
  };
}

/** Calls the API with the access token attached. */
export async function callApi<T>(path: string): Promise<T> {
  const token = user.value?.access_token;
  if (!token) throw new Error('Not authenticated');

  const response = await fetch(`${import.meta.env.VITE_API_BASE_URL}${path}`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) throw new Error(`${response.status} ${await response.text()}`);
  return response.json() as Promise<T>;
}
