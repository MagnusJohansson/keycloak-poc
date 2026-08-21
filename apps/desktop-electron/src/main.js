const { app, BrowserWindow, ipcMain, shell } = require('electron');
const path = require('node:path');
const http = require('node:http');
const crypto = require('node:crypto');

/**
 * OIDC for a desktop app, following RFC 8252 ("OAuth 2.0 for Native Apps").
 *
 * Two rules drive this whole file:
 *
 *  1. Authenticate in the USER'S BROWSER, never in a BrowserWindow you control.
 *     An in-app window can read the password as it is typed, cannot use the
 *     user's existing SSO session, and cannot show them the real URL bar to
 *     verify. The redirect comes back to a loopback HTTP server this process
 *     starts on an ephemeral port.
 *
 *  2. Tokens live in the MAIN process only. The renderer gets rendered data
 *     over a narrow IPC bridge and never sees a token, so an XSS bug in the UI
 *     cannot exfiltrate credentials.
 */

const ISSUER = process.env.DOCVAULT_ISSUER ?? 'http://localhost:8080/realms/docvault';
const CLIENT_ID = process.env.DOCVAULT_CLIENT_ID ?? 'docvault-desktop';
const API_BASE_URL = process.env.DOCVAULT_API_URL ?? 'http://localhost:5001';

/** Held in main-process memory only. Never sent to the renderer. */
let tokens = null;

function base64url(buffer) {
  return buffer.toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=/g, '');
}

async function discover() {
  const response = await fetch(`${ISSUER}/.well-known/openid-configuration`);
  if (!response.ok) {
    throw new Error(`OIDC discovery failed: ${response.status}. Is Keycloak running?`);
  }
  return response.json();
}

/**
 * Runs the authorization-code + PKCE flow against a loopback redirect.
 *
 * @param {string[]} acrValues Optional step-up request.
 */
async function authenticate(acrValues) {
  const config = await discover();

  // PKCE. Mandatory for a public client: the desktop app cannot keep a secret,
  // so proof of possession of the verifier is what binds the code to this client.
  const verifier = base64url(crypto.randomBytes(32));
  const challenge = base64url(crypto.createHash('sha256').update(verifier).digest());

  // CSRF protection for the callback.
  const state = base64url(crypto.randomBytes(16));

  return new Promise((resolve, reject) => {
    // Port 0 => the OS picks a free port. The Keycloak client registers
    // http://127.0.0.1:*/callback precisely so this works without pre-agreeing
    // on a port (see infra/terraform/20-realm/clients.tf).
    const server = http.createServer(async (req, res) => {
      const url = new URL(req.url, `http://127.0.0.1:${server.address().port}`);
      if (url.pathname !== '/callback') {
        res.writeHead(404).end();
        return;
      }

      const finish = (message) => {
        res.writeHead(200, { 'Content-Type': 'text/html' });
        res.end(`<html><body style="font-family:system-ui;padding:2rem"><h2>${message}</h2><p>You can close this tab.</p></body></html>`);
        server.close();
      };

      try {
        if (url.searchParams.get('state') !== state) {
          throw new Error('State mismatch - possible CSRF; discarding the response.');
        }

        const error = url.searchParams.get('error');
        if (error) {
          throw new Error(`${error}: ${url.searchParams.get('error_description') ?? ''}`);
        }

        const body = new URLSearchParams({
          grant_type: 'authorization_code',
          code: url.searchParams.get('code'),
          redirect_uri: `http://127.0.0.1:${server.address().port}/callback`,
          client_id: CLIENT_ID,
          code_verifier: verifier,
        });

        const response = await fetch(config.token_endpoint, {
          method: 'POST',
          headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
          body,
        });

        if (!response.ok) {
          throw new Error(`Token exchange failed: ${response.status} ${await response.text()}`);
        }

        tokens = await response.json();
        finish('Signed in to DocVault');
        resolve(tokens);
      } catch (e) {
        finish('Sign-in failed');
        reject(e);
      }
    });

    server.listen(0, '127.0.0.1', () => {
      const redirectUri = `http://127.0.0.1:${server.address().port}/callback`;
      const authUrl = new URL(config.authorization_endpoint);
      authUrl.searchParams.set('client_id', CLIENT_ID);
      authUrl.searchParams.set('response_type', 'code');
      authUrl.searchParams.set('redirect_uri', redirectUri);
      authUrl.searchParams.set('scope', 'openid profile email');
      authUrl.searchParams.set('state', state);
      authUrl.searchParams.set('code_challenge', challenge);
      authUrl.searchParams.set('code_challenge_method', 'S256');

      if (acrValues) {
        authUrl.searchParams.set('acr_values', acrValues);
        authUrl.searchParams.set('prompt', 'login');
      }

      // The system browser, not a BrowserWindow. This is the whole point.
      shell.openExternal(authUrl.toString());
    });
  });
}

/** Decodes a JWT payload for display. Does NOT validate it - the API does that. */
function decodeClaims(token) {
  const payload = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
  return JSON.parse(Buffer.from(payload, 'base64').toString('utf8'));
}

// --- IPC surface ------------------------------------------------------------
// Deliberately narrow: the renderer can ask for actions and for already-decoded
// claims, but there is no channel that returns a raw token.
ipcMain.handle('auth:signIn', async () => {
  await authenticate();
  return decodeClaims(tokens.access_token);
});

ipcMain.handle('auth:stepUp', async () => {
  await authenticate('silver');
  return decodeClaims(tokens.access_token);
});

ipcMain.handle('auth:signOut', async () => {
  tokens = null;
  return null;
});

ipcMain.handle('api:documents', async () => {
  if (!tokens) {
    return { error: 'Not signed in.' };
  }

  const response = await fetch(`${API_BASE_URL}/documents`, {
    headers: { Authorization: `Bearer ${tokens.access_token}` },
  });

  if (response.status === 403) {
    const challenge = response.headers.get('www-authenticate') ?? '';
    return {
      error: challenge.includes('insufficient_user_authentication')
        ? 'Step-up required.'
        : 'Forbidden: your account lacks the required role.',
    };
  }

  if (!response.ok) {
    return { error: `API returned ${response.status}` };
  }

  return response.json();
});

function createWindow() {
  const window = new BrowserWindow({
    width: 900,
    height: 700,
    webPreferences: {
      // The three settings that keep the renderer from touching Node or the
      // main process's memory. Turning any of them off would undo the isolation
      // that keeps tokens out of reach of page scripts.
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });

  window.loadFile(path.join(__dirname, 'index.html'));
}

app.whenReady().then(createWindow);
app.on('window-all-closed', () => process.platform !== 'darwin' && app.quit());
