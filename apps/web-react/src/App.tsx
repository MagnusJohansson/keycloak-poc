import { Navigate, NavLink, Route, Routes } from 'react-router-dom';
import { useAuth } from 'react-oidc-context';
import { ProtectedRoute, useRoles } from './auth/guards';
import { TokenInspector } from './components/TokenInspector';
import { DocumentsPage } from './pages/Documents';
import { ClassifiedPage } from './pages/Classified';
import { ProfilePage } from './pages/Profile';

export default function App() {
  const auth = useAuth();
  const roles = useRoles();

  if (auth.isLoading) {
    return <main className="shell"><p className="muted">Loading…</p></main>;
  }

  if (!auth.isAuthenticated) {
    return (
      <main className="shell landing">
        <h1>DocVault</h1>
        <p className="muted">A Keycloak identity lab.</p>
        <button onClick={() => void auth.signinRedirect()}>Sign in</button>
        <p className="muted small">
          Try <code>alice</code> (editor), <code>bob</code> (other tenant),{' '}
          <code>carol</code> (admin, MFA), or <code>dave</code> (no roles).
        </p>
      </main>
    );
  }

  return (
    <div className="layout">
      <main className="shell">
        <nav>
          <NavLink to="/">Documents</NavLink>
          <NavLink to="/classified">Classified</NavLink>
          <NavLink to="/profile">Profile</NavLink>
          <span className="spacer" />
          <span className="muted">
            {auth.user?.profile.preferred_username} · {roles.join(', ') || 'no roles'}
          </span>
          {/* End the Keycloak session too, not just the local one. Without
              post-logout redirect the user stays signed in at the IdP and the
              next "sign in" silently logs them straight back in. */}
          <button onClick={() => void auth.signoutRedirect()}>Sign out</button>
        </nav>

        <Routes>
          <Route path="/" element={<ProtectedRoute><DocumentsPage /></ProtectedRoute>} />
          <Route path="/classified" element={<ProtectedRoute><ClassifiedPage /></ProtectedRoute>} />
          <Route path="/profile" element={<ProtectedRoute><ProfilePage /></ProtectedRoute>} />
          {/* By the time this renders, react-oidc-context has finished the code
              exchange (App shows a loading state until then). Redirect off the
              callback URL via the router - history.replaceState alone changes the
              address bar without telling React Router, leaving the user parked on
              "Completing sign in…" forever. */}
          <Route path="/callback" element={<Navigate to="/" replace />} />
        </Routes>
      </main>
      <TokenInspector />
    </div>
  );
}
