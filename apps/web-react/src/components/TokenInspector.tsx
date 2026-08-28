// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

import { useAuth } from 'react-oidc-context';
import { useState } from 'react';

function decodeSegment(token: string, index: number): Record<string, unknown> | null {
  try {
    const segment = token.split('.')[index];
    return JSON.parse(atob(segment.replace(/-/g, '+').replace(/_/g, '/')));
  } catch {
    return null;
  }
}

/**
 * Renders the decoded access token beside the UI that reacts to it.
 *
 * This is the single most useful teaching device in the lab. Almost every
 * "why am I getting a 403?" question is answered by looking at `realm_access`,
 * `resource_access`, `aud` and `acr` together - and by noticing which one is
 * missing. Reading them in a debugger is possible; having them on screen while
 * clicking around makes the authorization model click.
 *
 * A production app would not ship this.
 */
export function TokenInspector() {
  const auth = useAuth();
  const [tab, setTab] = useState<'access' | 'id'>('access');

  if (!auth.user) {
    return null;
  }

  const token = tab === 'access' ? auth.user.access_token : auth.user.id_token;
  const claims = token ? decodeSegment(token, 1) : null;
  const header = token ? decodeSegment(token, 0) : null;

  const expiresAt = typeof claims?.exp === 'number' ? new Date(claims.exp * 1000) : null;
  const secondsLeft = expiresAt ? Math.round((expiresAt.getTime() - Date.now()) / 1000) : null;

  return (
    <aside className="inspector" data-testid="inspector">
      <header>
        <h2>Token inspector</h2>
        <div className="tabs">
          <button className={tab === 'access' ? 'active' : ''} onClick={() => setTab('access')}>
            Access token
          </button>
          <button className={tab === 'id' ? 'active' : ''} onClick={() => setTab('id')}>
            ID token
          </button>
        </div>
      </header>

      {secondsLeft !== null && (
        <p className={secondsLeft < 60 ? 'warn' : 'muted'}>
          Expires in {secondsLeft}s{secondsLeft < 60 && ' — silent renewal should kick in shortly'}
        </p>
      )}

      <dl className="highlights">
        <dt>aud</dt>
        <dd>
          <code>{JSON.stringify(claims?.aud) ?? '—'}</code>
          <small>Must include docvault-api, or the API returns 401.</small>
        </dd>
        <dt>acr</dt>
        <dd>
          <code>{String(claims?.acr ?? '—')}</code>
          <small>silver = stepped up with OTP.</small>
        </dd>
        <dt>scope</dt>
        <dd>
          <code>{String(claims?.scope ?? '—')}</code>
        </dd>
        <dt>groups</dt>
        <dd>
          <code>{JSON.stringify(claims?.groups) ?? '—'}</code>
          <small>The API derives your tenant from this path.</small>
        </dd>
      </dl>

      <details>
        <summary>Header</summary>
        <pre>{JSON.stringify(header, null, 2)}</pre>
      </details>
      <details open>
        <summary>Payload</summary>
        <pre>{JSON.stringify(claims, null, 2)}</pre>
      </details>
    </aside>
  );
}
