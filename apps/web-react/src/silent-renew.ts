import { UserManager } from 'oidc-client-ts';

// Reads the authorization response from this iframe's URL and hands the new token
// to the parent window. The UserManager needs no configuration here: everything
// required is already in the callback URL and in sessionStorage.
new UserManager({ authority: '', client_id: '', redirect_uri: '' })
  .signinSilentCallback()
  .catch((error: unknown) => {
    // Swallowing this would leave the user silently logged out at the next API call.
    console.error('Silent token renewal failed', error);
  });
