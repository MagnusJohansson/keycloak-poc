# 7. Desktop integration (Electron & WinUI 3)

Two desktop clients, solving the same problem with different tools:

| | Electron | WinUI 3 / .NET 10 |
|---|---|---|
| Source | `apps/desktop-electron` | `apps/desktop-winui` |
| PKCE | hand-rolled, ~80 lines of Node built-ins | `Duende.IdentityModel.OidcClient` (certified) |
| Redirect | loopback `http://127.0.0.1/*` (registered) | **identical** |
| Browser | `shell.openExternal` | `Process.Start(UseShellExecute = true)` |
| Token isolation | main process only, never the renderer | in-process; DPAPI-encrypted at rest |

The redirect row is the point: it is byte-identical, because the pattern is a
property of *native apps*, not of any UI framework. Everything below applies to
both unless stated.

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

The Keycloak client registers `http://127.0.0.1/*`, so no port needs to be agreed
in advance: Keycloak applies RFC 8252 loopback handling and ignores the port for a
loopback host, while the trailing wildcard covers the path.

**Do not register `http://127.0.0.1:*/callback`.** Keycloak honours a wildcard only
at the *end* of a redirect URI, so a `*` in the port position is matched literally
and every authorization request is rejected with `invalid_request` — which reads
like a client bug and is not one. See the comment in `20-realm/clients.tf`.

Use `127.0.0.1`, not `localhost` — the latter can resolve to IPv6 `::1` and miss
the listener.

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

### Configuring the Electron client

Settings live in **`apps/desktop-electron/config.json`**, which is gitignored so your
deployment URLs are not committed — copy the template first:

```bash
cp apps/desktop-electron/config.json.example apps/desktop-electron/config.json
```

Omitting the file entirely is fine: the app falls back to the local Docker lab.

```jsonc
{
  "issuer":     "http://localhost:8080/realms/docvault",   // must include /realms/<realm>
  "apiBaseUrl": "http://localhost:5001",
  "clientId":   "docvault-desktop",
  "stepUpAcr":  "silver"
}
```

Overridable with `DOCVAULT_ISSUER`, `DOCVAULT_API_URL`, `DOCVAULT_CLIENT_ID`.

> **It cannot share the React app's `.env`**, for three reasons: Vite only exposes
> `VITE_`-prefixed variables to the browser bundle and Electron's main process
> never reads `.env` at all; the names differ; and the two use **different
> Keycloak clients** (`docvault-desktop` vs `docvault-web-react`), so a shared
> `clientId` would be wrong for one of them.
>
> Separate clients is deliberate — it is what lets you revoke, scope and audit the
> desktop app independently of the SPA.

To point it at Azure, edit `config.json` or:

```bash
DOCVAULT_ISSUER="https://<your-keycloak>/realms/docvault" \
DOCVAULT_API_URL="https://<your-api>" \
  npm start
```

Settings are validated at startup, so a missing scheme or an issuer pointing at
the host root fails immediately rather than as an opaque browser error. The app
logs issuer, clientId, apiBaseUrl and the full authorize URL to the terminal.

## WinUI 3 / .NET 10

**Yes, WinUI 3 works with Keycloak** — it is an ordinary OAuth 2.0 public client.
Nothing about it is special; it does exactly what the Electron client does.

### Layout, and why it is split

```
apps/desktop-winui/
├── DocVault.Desktop.Auth/        net10.0    <- all the OIDC logic
├── DocVault.Desktop.Auth.Tests/  net10.0    <- 30 tests, run on Linux/macOS in CI
└── DocVault.WinUI/               net10.0-windows10.0.19041.0
```

WinUI XAML compiles only on Windows. Putting the OIDC logic in a plain `net10.0`
library means the part that can actually be *wrong* — PKCE, refresh, claim
parsing, step-up handling — is unit-tested on every platform, while only the
button-wiring needs a Windows runner. The `winui` job in CI builds the shell on
`windows-latest`.

`apps/api-dotnet/DocVault.slnx` deliberately does **not** reference the WinUI
project: that solution is built on Linux in CI and a Windows-only TFM would break it.

### The library

`Duende.IdentityModel.OidcClient` (Apache-2.0, RFC 8252 certified) handles
discovery, PKCE, and refresh. PKCE is always on and cannot be disabled — correct
for a public client.

```csharp
_client = new OidcClient(new OidcClientOptions
{
    Authority   = "http://localhost:8080/realms/docvault",
    ClientId    = "docvault-winui",
    RedirectUri = _browser.RedirectUri,     // http://127.0.0.1:{ephemeral}/callback
    Scope       = "openid profile email",
    Browser     = new LoopbackBrowser(),
    Policy = new Policy
    {
        Discovery = new DiscoveryPolicy { RequireHttps = !IsLoopback(authority) },
    },
});
```

> **The one setting that will catch you out.** OidcClient refuses plain-HTTP
> discovery by default. Against the local lab (`http://localhost:8080`) it fails
> before reaching Keycloak at all, and the error names the *policy*, not the URL —
> which sends you looking in entirely the wrong place. Relax it only for loopback;
> the sample derives that from the authority rather than hardcoding it.

### Step-up

Same ACR contract as every other client:

```csharp
await _client.LoginAsync(new LoginRequest
{
    FrontChannelExtraParameters = new Parameters
    {
        { "acr_values", "silver" },
        { "prompt", "login" },     // without this the SSO cookie satisfies it silently
    },
});
```

### Token storage

DPAPI (`ProtectedData`, `DataProtectionScope.CurrentUser`), which ties the
ciphertext to the signed-in Windows account.

`Windows.Security.Credentials.PasswordVault` is the nicer API but needs a
**packaged** (MSIX) app and throws for unpackaged ones. This sample runs
unpackaged (`<WindowsPackageType>None</WindowsPackageType>`) so it starts with
`dotnet run`; if you ship MSIX, prefer `PasswordVault`.

### Running it

```powershell
make seed                     # registers the docvault-winui client
dotnet run --project apps/desktop-winui/DocVault.WinUI
```

### Configuration

Settings live in **`DocVault.WinUI/appsettings.json`**, copied next to the
executable at build time:

```jsonc
{
  "Authority":  "http://localhost:8080/realms/docvault",   // must include /realms/<realm>
  "ApiBaseUrl": "http://localhost:5001",
  "ClientId":   "docvault-winui",
  "StepUpAcr":  "silver"
}
```

Any value can be overridden with a `DOCVAULT_`-prefixed environment variable
(`DOCVAULT_AUTHORITY`, `DOCVAULT_APIBASEURL`), which wins over the file.

The file is the primary mechanism on purpose: a GUI app launched from the Start
menu has nowhere to pick up environment variables. The override exists for CI and
for running two instances against different realms.

Loading and validation are in `DocVault.Desktop.Auth`, not the XAML project, so
they are unit-tested on every platform. Validation catches the two slips that
otherwise surface much later as opaque discovery errors: an authority missing its
scheme, and one pointing at the host root rather than `/realms/<realm>`.

Full detail in [apps/desktop-winui/README.md](../apps/desktop-winui/README.md).

### Two alternatives worth knowing

**Windows App SDK `OAuth2Manager`** is Microsoft's first-party answer — it always
uses the system browser and follows RFC 8252. It is not used here because it ships
only in the **experimental** channel (a prerelease `-experimental`
`Microsoft.WindowsAppSDK`), and its custom-scheme redirect wants MSIX packaging for
protocol activation. Revisit it when it reaches stable; the swap would be confined
to `LoopbackBrowser` and `KeycloakDesktopClient`.

**MSAL + WAM** is the better choice *if* every user signs in with a Microsoft Entra
ID account and you want silent SSO from the Windows session, plus Windows Hello and
conditional access. If you need both corporate and non-corporate identities, keep
Keycloak and broker Entra into it instead — see
[uc2](use-cases/uc2-enterprise-sso-entra.md).

---

Next: [8. Backend integration](07-integration-backend.md).
