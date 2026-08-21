import { useAuth } from 'react-oidc-context';
import { useCallback } from 'react';

/**
 * Re-runs the authorization request demanding a higher authentication level.
 *
 * `acr_values` is what asks Keycloak for step-up. The realm translates it to a
 * Level of Authentication via its `acr.loa.map` attribute, and the browser flow
 * then requires OTP. If that map is missing, Keycloak ignores `acr_values`
 * silently and the user is returned with the same low ACR - which looks like this
 * hook is broken when the fault is in the realm configuration.
 *
 * `prompt: 'login'` forces a fresh interaction rather than letting the existing
 * SSO cookie satisfy the request - the whole point of a step-up is a *new*
 * assertion of presence.
 */
export function useStepUp() {
  const auth = useAuth();

  return useCallback(
    (requiredAcr: string) =>
      auth.signinRedirect({
        extraQueryParams: { acr_values: requiredAcr },
        prompt: 'login',
        // Come back to where the user was, not to the home page.
        state: { returnTo: window.location.pathname },
      }),
    [auth],
  );
}
