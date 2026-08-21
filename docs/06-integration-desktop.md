# 8. Desktop integration (Electron)

Source: `apps/desktop-electron`.

## The two rules

### 1. Loopback redirect, system browser

Per RFC 8252, a desktop app authenticates in the *user's* browser and receives the
redirect on a loopback HTTP server it starts on an ephemeral port:

```js
const server = http.createServer(/* … */);
server.listen(0, '127.0.0.1', () => {          // port 0 = OS picks a free one
  shell.openExternal(authUrl.toString());       // the REAL browser
});
```

The Keycloak client registers `http://127.0.0.1:*/callback` precisely so no port
needs to be agreed in advance. Use `127.0.0.1`, not `localhost` — the latter can
resolve to IPv6 `::1` and miss the listener.

### 2. Tokens never enter the renderer

This is the part most Electron OIDC examples get wrong. Tokens live in the **main
process**. The renderer talks over a narrow, explicitly enumerated IPC bridge:

```js
contextBridge.exposeInMainWorld('docvault', {
  signIn: () => ipcRenderer.invoke('auth:signIn'),      // returns decoded CLAIMS
  getDocuments: () => ipcRenderer.invoke('api:documents'),
});
```

Note what is absent: any channel returning a raw token. The renderer gets already-
decoded claims for display and data fetched on its behalf, so a script injected
into the page has nothing to steal.

Backed by three `webPreferences` settings, none of which are optional:

```js
contextIsolation: true,   // preload and page get separate JS contexts
nodeIntegration: false,   // no require() in the page
sandbox: true,            // OS-level renderer sandbox
```

Turning any one off collapses the isolation the design depends on.

## PKCE by hand

The client has no runtime dependencies — the flow is ~80 lines of Node built-ins:

```js
const verifier  = base64url(crypto.randomBytes(32));
const challenge = base64url(crypto.createHash('sha256').update(verifier).digest());
const state     = base64url(crypto.randomBytes(16));
```

`state` is checked on the callback and the response discarded on mismatch — the
CSRF defence for the redirect. Writing it out is more instructive here than
delegating to a library, and it is short enough to audit.

## Running

```bash
cd apps/desktop-electron
npm install
DOCVAULT_ISSUER=http://localhost:8080/realms/docvault npm start
```

## For a real product

- **Persist tokens in the OS credential store** (`keytar`, or Electron's
  `safeStorage`). This lab keeps them in memory, so a restart means signing in again.
- **Sign and notarise the app.** An unsigned desktop client that asks for
  credentials is indistinguishable from malware.
- **Keep the CSP.** `index.html` sets `default-src 'self'`; no remote content, no
  inline eval.

---

Next: [9. Backend integration](07-integration-backend.md).
