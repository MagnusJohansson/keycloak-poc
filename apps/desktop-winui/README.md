# DocVault desktop — WinUI 3 / .NET 10

A native Windows client for the DocVault API, authenticating against Keycloak
with authorization code + PKCE through the system browser.

```
DocVault.Desktop.Auth/        net10.0    all the OIDC logic - builds and tests anywhere
DocVault.Desktop.Auth.Tests/  net10.0    45 tests, run on Linux/macOS in CI
DocVault.WinUI/               net10.0-windows10.0.19041.0    thin XAML shell
```

WinUI XAML compiles only on Windows, so the logic that can actually be wrong —
PKCE, refresh, claim parsing, step-up handling, settings validation — lives in a
plain `net10.0` library that is unit-tested on every platform. Only the button
wiring needs a Windows runner.

## Configuration

Edit **`DocVault.WinUI/appsettings.json`**. It is copied next to the executable at
build time, so you can also edit it after publishing without rebuilding.

| Setting | Default | What it is |
|---|---|---|
| `Authority` | `http://localhost:8080/realms/docvault` | The realm issuer. **Must include `/realms/<realm>`** |
| `ApiBaseUrl` | `http://localhost:5001` | Where the DocVault API is |
| `ClientId` | `docvault-winui` | Must match a client in the realm (`20-realm/clients.tf`) |
| `Scope` | `openid profile email` | Requested scopes |
| `StepUpAcr` | `silver` | The level the API demands for classified documents |

Any of them can be overridden with a `DOCVAULT_`-prefixed environment variable,
which takes precedence over the file:

```powershell
$env:DOCVAULT_AUTHORITY = "https://your-keycloak/realms/docvault"
$env:DOCVAULT_APIBASEURL = "https://your-api"
```

Use the file for normal configuration — a GUI app launched from the Start menu
has nowhere to pick up environment variables. Use the variables for CI, or to run
two instances against different realms.

Settings are validated at startup, so a missing `https://` or an authority
pointing at the host root instead of the realm fails immediately with a clear
message rather than as an opaque discovery error later.

### Pointing at Azure

```jsonc
{
  "Authority": "https://ca-keycloak.<suffix>.azurecontainerapps.io/realms/docvault",
  "ApiBaseUrl": "https://ca-docvault-api.<suffix>.azurecontainerapps.io"
}
```

No code changes and no rebuild of the library — the same binary works against the
local lab and the cloud.

## Running it

```powershell
# from the repo root, with the realm seeded (make seed)
dotnet run --project apps/desktop-winui/DocVault.WinUI
```

**Windows only.** The XAML will not compile on macOS or Linux; the `winui` CI job
builds it on `windows-latest`.

## Working in Visual Studio

### Choosing a platform

WinUI cannot build as **Any CPU** — the Windows App SDK ships native binaries, so
`DocVault.WinUI` declares `x86;x64;ARM64`. The solution maps them explicitly:

| Solution platform | DocVault.WinUI builds as |
|---|---|
| Any CPU | x64 |
| x64 | x64 |
| x86 | x86 |
| **ARM64** | **ARM64** |

On an ARM64 machine (Snapdragon, or Windows on Apple silicon) pick **ARM64** from
the platform dropdown for a native build. The default maps to x64 because an x64
build still runs on ARM64 under emulation, whereas an ARM64 build will not run on
x64 at all.

The two library projects are ordinary AnyCPU .NET libraries and need no mapping.

> Without those mappings Visual Studio reports *"specifies a project configuration
> for DocVault.WinUI.csproj that does not exist for that project"* — it invents an
> Any CPU solution platform and finds nothing to map it to.



Visual Studio generates several things that must not be committed; `.gitignore`
covers them, but two are worth knowing about:

- **`<AppName>_TemporaryKey.pfx`** — created automatically the moment you enable
  MSIX packaging. It is a **private signing key**. Ignored here, but if you ever
  see one appear in `git status`, do not commit it: a key in git history cannot be
  un-published.
- **`.vs/`** — per-developer editor state and a SQLite index, regenerated on open.

Also ignored: `Generated Files/` (XAML codegen), `AppPackages/` and
`BundleArtifacts/` (MSIX output, large and reproducible).

## Logging and diagnostics

Every run writes to three places at once, because a GUI app has no console and
the interesting failures happen in a browser round-trip you cannot step through:

| Sink | Where |
|---|---|
| **File** | `%LOCALAPPDATA%\DocVault\logs\docvault-<timestamp>.log` — one per run, 20 kept |
| **Debug** | Visual Studio **Output** window |
| **Console** | stdout, if you launched it from a terminal |

The app shows the current log path in its status bar at startup, and appends it
to any error message.

Duende's own OIDC diagnostics go to the same sinks, so the log contains the full
authorize URL. That is usually the fastest way to diagnose a sign-in failure —
paste it into a browser and the provider will tell you what it objects to:

```
10:47:02.113 INF KeycloakDesktopClient  Authority   http://localhost:8080/realms/docvault
10:47:02.115 INF KeycloakDesktopClient  ClientId    docvault-winui
10:47:02.115 INF KeycloakDesktopClient  RedirectUri http://127.0.0.1:51234/callback
10:47:02.230 INF LoopbackBrowser        Opening system browser: http://localhost:8080/realms/...
```

### `invalid_request` at sign-in

Nearly always the provider rejecting the **redirect URI**, not a client bug. The
log prints the exact URI the app listened on; compare it with the client's
registered `valid_redirect_uris`.

> A trap worth knowing: Keycloak only honours a wildcard at the **end** of a
> redirect URI. `http://127.0.0.1:*/callback` looks reasonable and never matches —
> the `*` is compared literally. Use `http://127.0.0.1/*`, which works because
> Keycloak applies RFC 8252 loopback handling and ignores the port for a loopback
> host. See `infra/terraform/20-realm/clients.tf`.

## Redirect URI

The app listens on `http://127.0.0.1:<ephemeral>/callback`, and the
`docvault-winui` client registers `http://127.0.0.1/*` to match it. An ephemeral
port is the RFC 8252 pattern for native apps — a fixed port would collide with
other software and prevent two instances — and Keycloak ignores the port for a
loopback host, so the registered value does not name one.

Note the registered value is **not** `http://127.0.0.1:*/callback`; see the trap
above for why that never matches.

See [docs/06-integration-desktop.md](../../docs/06-integration-desktop.md) for the
design notes, and how this compares with the Electron client.
