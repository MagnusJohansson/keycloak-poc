// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

import { useState } from 'react';
import { useAuth } from 'react-oidc-context';
import { ApiError, StepUpRequiredError, callApi } from '../auth/api';
import { useStepUp } from '../auth/useStepUp';

interface ClassifiedResponse {
  tenant: string;
  acr: string | null;
  documents: { id: string; title: string }[];
}

/**
 * The step-up demo, end to end:
 *   fetch -> 401 + WWW-Authenticate -> re-authenticate with acr_values -> retry.
 */
export function ClassifiedPage() {
  const auth = useAuth();
  const stepUp = useStepUp();
  const [data, setData] = useState<ClassifiedResponse | null>(null);
  const [challenge, setChallenge] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function load() {
    setError(null);
    setChallenge(null);
    try {
      setData(await callApi<ClassifiedResponse>(auth.user, '/documents/classified'));
    } catch (e) {
      if (e instanceof StepUpRequiredError) {
        // Recoverable. Offer the user the step-up rather than redirecting
        // immediately - an unexplained jump to a login screen is alarming.
        setChallenge(e.requiredAcr);
        return;
      }
      setError(e instanceof ApiError ? `${e.status}: ${e.message}` : String(e));
    }
  }

  return (
    <section>
      <h1>Classified documents</h1>
      <p className="muted">
        Requires the <code>doc.admin</code> role <em>and</em> a recent MFA challenge.
      </p>

      <button onClick={load}>Load classified documents</button>

      {challenge && (
        <div className="notice" data-testid="stepup-challenge">
          <strong>Additional verification required.</strong>
          <p className="muted">
            The API rejected your token because it was issued at a lower authentication
            level. Re-authenticating with <code>acr_values={challenge}</code> will prompt
            for your one-time code.
          </p>
          <button onClick={() => void stepUp(challenge)}>Verify with OTP</button>
        </div>
      )}

      {error && <p className="error">{error}</p>}

      {data && (
        <>
          <p className="success">
            Access granted at <code>acr={data.acr}</code>
          </p>
          <ul className="documents">
            {data.documents.map((d) => (
              <li key={d.id}>{d.title}</li>
            ))}
          </ul>
        </>
      )}
    </section>
  );
}
