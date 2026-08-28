// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

import { authorize, refresh, type AuthConfiguration, type AuthorizeResult } from 'react-native-app-auth';
import * as Keychain from 'react-native-keychain';

/**
 * OIDC for React Native.
 *
 * react-native-app-auth wraps AppAuth-iOS / AppAuth-Android, so this uses the
 * same native machinery as the Flutter client: the system browser
 * (ASWebAuthenticationSession / Chrome Custom Tabs), PKCE always on, and a
 * custom-scheme redirect the OS routes back to the app.
 *
 * Registering the scheme is a NATIVE change on both platforms, and forgetting it
 * is the usual reason sign-in appears to hang:
 *   android/app/build.gradle  ->  manifestPlaceholders = [appAuthRedirectScheme: 'io.docvault.rn']
 *   ios/<App>/Info.plist      ->  CFBundleURLTypes / CFBundleURLSchemes
 */
const config: AuthConfiguration = {
  // Everything else is discovered from /.well-known/openid-configuration, so
  // switching to the Azure deployment is a one-line change.
  issuer: process.env.DOCVAULT_ISSUER ?? 'http://10.0.2.2:8080/realms/docvault',
  clientId: 'docvault-reactnative',
  redirectUrl: 'io.docvault.rn://oauth/callback',
  scopes: ['openid', 'profile', 'email'],

  // Explicit, though the library defaults to true for public clients. Stated
  // here because it is the single most important setting in this file.
  usePKCE: true,
};

const TOKEN_SERVICE = 'io.docvault.tokens';

/**
 * Persists tokens in the platform keystore (Keychain / Android Keystore).
 *
 * AsyncStorage would be the obvious choice and is the wrong one: it is an
 * unencrypted file, readable on a compromised device.
 */
async function persist(result: AuthorizeResult): Promise<void> {
  await Keychain.setGenericPassword(
    'docvault',
    JSON.stringify({
      accessToken: result.accessToken,
      refreshToken: result.refreshToken,
      expiresAt: result.accessTokenExpirationDate,
    }),
    { service: TOKEN_SERVICE, accessible: Keychain.ACCESSIBLE.WHEN_UNLOCKED_THIS_DEVICE_ONLY },
  );
}

export async function signIn(): Promise<AuthorizeResult> {
  const result = await authorize(config);
  await persist(result);
  return result;
}

/** Requests a higher authentication level (step-up, use-case 3). */
export async function stepUp(acr = 'silver'): Promise<AuthorizeResult> {
  const result = await authorize({
    ...config,
    additionalParameters: { acr_values: acr, prompt: 'login' },
  });
  await persist(result);
  return result;
}

/** Returns a usable access token, refreshing when the stored one has expired. */
export async function currentAccessToken(): Promise<string | null> {
  const stored = await Keychain.getGenericPassword({ service: TOKEN_SERVICE });
  if (!stored) {
    return null;
  }

  const { accessToken, refreshToken, expiresAt } = JSON.parse(stored.password);

  if (expiresAt && new Date(expiresAt) > new Date()) {
    return accessToken;
  }

  if (!refreshToken) {
    return null;
  }

  try {
    const refreshed = await refresh(config, { refreshToken });
    await persist({ ...refreshed, refreshToken: refreshed.refreshToken ?? refreshToken } as AuthorizeResult);
    return refreshed.accessToken;
  } catch {
    // The realm has refresh-token reuse detection enabled, so a revoked or
    // replayed token ends the session. Clear state rather than retry-looping.
    await signOut();
    return null;
  }
}

export async function signOut(): Promise<void> {
  await Keychain.resetGenericPassword({ service: TOKEN_SERVICE });
}

/** Mirrors KeycloakClaimsTransformation on the server. */
export function rolesFrom(claims: Record<string, any>, apiClientId = 'docvault-api'): string[] {
  const realm: string[] = claims.realm_access?.roles ?? [];
  const client: string[] = claims.resource_access?.[apiClientId]?.roles ?? [];
  return [...realm, ...client].filter((role) => !role.startsWith('default-roles-'));
}

export function decodeClaims(accessToken: string): Record<string, any> {
  const payload = accessToken.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
  return JSON.parse(globalThis.atob(payload));
}
