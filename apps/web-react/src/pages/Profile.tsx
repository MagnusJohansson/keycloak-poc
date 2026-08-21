import { useEffect, useState } from 'react';
import { useAuth } from 'react-oidc-context';
import { callApi } from '../auth/api';

/**
 * Shows the API's view of the caller next to the browser's.
 *
 * When the two disagree - the SPA thinks you are a doc.editor but /me says
 * otherwise - the cause is almost always a missing protocol mapper or a stale
 * token, and seeing both side by side makes that obvious.
 */
export function ProfilePage() {
  const auth = useAuth();
  const [me, setMe] = useState<unknown>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    callApi(auth.user, '/me').then(setMe).catch((e) => setError(String(e)));
  }, [auth.user]);

  return (
    <section>
      <h1>Profile</h1>
      <p className="muted">What the API sees when you call it — not what the browser believes.</p>
      {error && <p className="error">{error}</p>}
      <pre>{JSON.stringify(me, null, 2)}</pre>
    </section>
  );
}
