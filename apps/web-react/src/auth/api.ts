// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

import type { User } from 'oidc-client-ts';
import { apiBaseUrl } from './oidcConfig';

/** Raised when the API demands a higher authentication level (RFC 9470). */
export class StepUpRequiredError extends Error {
  readonly requiredAcr: string;

  constructor(requiredAcr: string) {
    super(`Step-up authentication required: acr_values=${requiredAcr}`);
    this.name = 'StepUpRequiredError';
    this.requiredAcr = requiredAcr;
  }
}

export class ApiError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

/**
 * Parses the `WWW-Authenticate` header the API sends with a step-up challenge.
 *
 * Example: Bearer error="insufficient_user_authentication", acr_values="silver"
 */
function parseStepUpChallenge(header: string | null): string | null {
  if (!header || !header.includes('insufficient_user_authentication')) {
    return null;
  }
  return /acr_values="([^"]+)"/.exec(header)?.[1] ?? null;
}

/**
 * Calls the DocVault API with the caller's access token attached.
 *
 * Token *renewal* is deliberately not handled here - react-oidc-context's
 * `automaticSilentRenew` already does it in the background. Retrying inside the
 * fetch wrapper as well would produce two competing refresh attempts, and with
 * refresh-token reuse detection enabled on the realm the second one invalidates
 * the session. One owner for renewal, and only one.
 */
export async function callApi<T>(user: User | null | undefined, path: string, init?: RequestInit): Promise<T> {
  if (!user?.access_token) {
    throw new ApiError(401, 'Not authenticated.');
  }

  const response = await fetch(`${apiBaseUrl}${path}`, {
    ...init,
    headers: {
      ...init?.headers,
      Authorization: `Bearer ${user.access_token}`,
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
    },
  });

  // RFC 9470 sends the step-up challenge as a 401, so the header must be read BEFORE
  // any "401 means the session expired" handling gets a chance to swallow it. 403 is
  // accepted too: some resource servers use it, and the header is what decides.
  if (response.status === 401 || response.status === 403) {
    const requiredAcr = parseStepUpChallenge(response.headers.get('WWW-Authenticate'));
    if (requiredAcr) {
      // Recoverable: the user can re-authenticate at a higher level. Distinguished
      // from a plain 403, which no amount of re-authenticating will fix.
      throw new StepUpRequiredError(requiredAcr);
    }
  }

  if (!response.ok) {
    throw new ApiError(response.status, await response.text().catch(() => response.statusText));
  }

  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}
