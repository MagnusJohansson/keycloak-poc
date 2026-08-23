# DocVault desktop — WinUI 3 / .NET 10

A native Windows client for the DocVault API, authenticating against Keycloak
with authorization code + PKCE through the system browser.

```
DocVault.Desktop.Auth/        net10.0    all the OIDC logic - builds and tests anywhere
DocVault.Desktop.Auth.Tests/  net10.0    39 tests, run on Linux/macOS in CI
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

Visual Studio generates several things that must not be committed; `.gitignore`
covers them, but two are worth knowing about:

- **`<AppName>_TemporaryKey.pfx`** — created automatically the moment you enable
  MSIX packaging. It is a **private signing key**. Ignored here, but if you ever
  see one appear in `git status`, do not commit it: a key in git history cannot be
  un-published.
- **`.vs/`** — per-developer editor state and a SQLite index, regenerated on open.

Also ignored: `Generated Files/` (XAML codegen), `AppPackages/` and
`BundleArtifacts/` (MSIX output, large and reproducible).

## Redirect URI

The app listens on `http://127.0.0.1:<ephemeral>/callback`, which is why the
`docvault-winui` client registers `http://127.0.0.1:*/callback`. A wildcard port
is explicitly permitted for native apps — a fixed port would collide with other
software and prevent two instances.

See [docs/06-integration-desktop.md](../../docs/06-integration-desktop.md) for the
design notes, and how this compares with the Electron client.
