// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

import type { ReactNode } from 'react';
import { useAuth } from 'react-oidc-context';

/**
 * Requires an authenticated user, redirecting to Keycloak if there is none.
 *
 * Note this is a USABILITY control, not a security control. Anyone can edit the
 * JavaScript running in their own browser. The API enforces the real boundary; this
 * only spares the user a pointless 401.
 */
export function ProtectedRoute({ children }: { children: ReactNode }) {
  const auth = useAuth();

  if (auth.isLoading) {
    return <p className="muted">Checking your session…</p>;
  }

  if (auth.error) {
    return <p className="error">Authentication error: {auth.error.message}</p>;
  }

  if (!auth.isAuthenticated) {
    void auth.signinRedirect({ state: { returnTo: window.location.pathname } });
    return <p className="muted">Redirecting to sign in…</p>;
  }

  return <>{children}</>;
}

/**
 * Hides UI the user's roles do not permit.
 *
 * Renders an explanation rather than redirecting: sending an authenticated-but-
 * unauthorized user back to a login page is the browser equivalent of answering
 * 403 with 401, and traps them in a loop that logging in again cannot break.
 */
export function RequireRole({ anyOf, children }: { anyOf: string[]; children: ReactNode }) {
  const auth = useAuth();
  const roles = useRoles();

  if (!auth.isAuthenticated) {
    return null;
  }

  if (!anyOf.some((role) => roles.includes(role))) {
    return (
      <div className="notice" data-testid="role-denied">
        <strong>Not available to your account.</strong>
        <p className="muted">
          Requires one of: <code>{anyOf.join(', ')}</code>. You have:{' '}
          <code>{roles.length ? roles.join(', ') : 'no roles'}</code>.
        </p>
      </div>
    );
  }

  return <>{children}</>;
}

/**
 * Reads the caller's effective roles out of the access token.
 *
 * This mirrors what KeycloakClaimsTransformation does server-side: realm roles from
 * `realm_access`, plus this API's roles from `resource_access["docvault-api"]`.
 * Roles belonging to other clients are ignored, exactly as the API ignores them.
 */
export function useRoles(): string[] {
  const auth = useAuth();
  const token = auth.user?.access_token;
  if (!token) {
    return [];
  }

  try {
    const payload = JSON.parse(atob(token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/')));
    const realmRoles: string[] = payload.realm_access?.roles ?? [];
    const clientRoles: string[] = payload.resource_access?.['docvault-api']?.roles ?? [];
    return [...realmRoles, ...clientRoles].filter((r) => !r.startsWith('default-roles-'));
  } catch {
    return [];
  }
}
