// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

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
