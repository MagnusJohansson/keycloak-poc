# DocVault mobile — Flutter

Signs in to Keycloak with authorization code + PKCE through the **system browser**
(`flutter_appauth`), storing tokens in the platform keystore.

## Configuration — read this first

Flutter configuration is **compile-time**. `String.fromEnvironment` is resolved
when the app is *built*, so setting an environment variable before running the app
has no effect at all. This is the single most surprising thing about configuring a
Flutter app, and it is different from every other client in this repo.

Values come from a JSON file passed at build/run time:

```bash
flutter run --dart-define-from-file=config/azure.json
```

| File | For |
|---|---|
| `config/local.json` | Android emulator → local lab (`10.0.2.2`) |
| `config/local-ios.json` | iOS simulator → local lab (`localhost`) |
| `config/azure.json.example` | copy to `config/azure.json` and fill in your URLs |

**Changing a value requires a rebuild**, not just a restart.

Individual overrides win over the file:

```bash
flutter run --dart-define-from-file=config/local.json \
            --dart-define=CLIENT_ID=some-other-client
```

## Which host address?

The lab runs on your machine, which is not `localhost` from inside a device:

| Target | Use |
|---|---|
| Android emulator | `http://10.0.2.2:8080` — the emulator's alias for the host |
| iOS simulator | `http://localhost:8080` — the simulator shares the host network |
| Physical device | `http://<your-LAN-ip>:8080`, on the same network |
| Azure | the HTTPS URL — works identically from all three |

`localhost` inside an Android emulator is the emulator itself. That is the usual
reason the app "cannot reach Keycloak".

> **Testing against Azure is simpler than against the local lab**: one HTTPS URL
> that works from every device, and no cleartext-HTTP exceptions needed.

## Running from VS Code

Press **F5** and pick one of:

- `Flutter — local lab (iOS simulator)`
- `Flutter — local lab (Android emulator)`
- `Flutter — Azure`

Each passes the right `--dart-define-from-file` for you, which is the part that
is easy to forget — and forgetting it silently gives you the built-in defaults
rather than an error.

`Flutter — Azure` needs `config/azure.json`; copy `config/azure.json.example`.
That file is gitignored so your deployment URLs are not committed.

## Running

```bash
# iOS simulator, local lab
open -a Simulator
flutter run --dart-define-from-file=config/local-ios.json

# Android emulator, local lab
flutter emulators --launch Pixel_10
flutter run --dart-define-from-file=config/local.json

# either, against Azure
cp config/azure.json.example config/azure.json   # then edit
flutter run --dart-define-from-file=config/azure.json
```

Sign in as `alice` / `DocVaultLab!2026`.

## Cleartext HTTP for the local lab

iOS and Android both block plain HTTP by default, so the local lab needs an
exception. Both are configured, and both are **scoped to loopback and the emulator
host alias** rather than opened globally:

- iOS — `NSExceptionDomains` for `localhost` and `10.0.2.2` in `Info.plist`, not
  `NSAllowsArbitraryLoads`
- Android — `res/xml/network_security_config.xml` limited to the same two domains,
  not `usesCleartextTraffic="true"`

Neither is needed for Azure. Remove both before shipping anything real.

## Troubleshooting

### Sign-in hangs after entering the password — no error, no toast

Check logcat for:

```
W/AppAuth: No stored state - unable to handle response
```

That means the browser redirect came back but AppAuth could not match it to the
pending authorization request, so `authorizeAndExchangeCode` never completes. It
throws nothing, which is why the UI just sits there.

On Android the cause is a **task-affinity mismatch**. Flutter's template sets
`android:taskAffinity=""` on `MainActivity`. An empty affinity means "no affinity
to any task", so that activity starts its own — while AppAuth's activities, which
inherit the default package-name affinity, get theirs. All three end up in
separate tasks, so the redirect cannot resume the `singleTask`
`AuthorizationManagementActivity` holding the pending request.

**The fix is to remove `android:taskAffinity=""` from `MainActivity`**, which this
manifest does, so all three share the default affinity.

Setting `""` on AppAuth's activities as well does *not* work: an empty affinity is
not a *shared* affinity. Confirmed on an emulator — `dumpsys activity activities`
showed tasks #58, #59 and #60 for the three activities; after the fix they share
one task with `numActivities=2`:

```bash
adb shell dumpsys activity activities | grep -E "Task\{.*docvault"
```

### `IllegalArgumentException: only https connections are permitted`

AppAuth rejects plain HTTP in its `DefaultConnectionBuilder`, **before** Android's
cleartext policy is ever consulted — so the ATS and network-security-config
exceptions are necessary but not sufficient for the local lab.

`DocVaultAuth` therefore passes `allowInsecureConnections: true` when, and only
when, the issuer is `http`. A real HTTPS deployment can never silently accept an
unencrypted discovery document, which would let an attacker serve their own
signing keys.

Other causes worth ruling out:

- **Developer options → "Don't keep activities"** destroys the activity while the
  browser is in front. Check with
  `adb shell settings get global always_finish_activities` — `1` means it is on.
- The scheme in `AndroidManifest` / `Info.plist` not matching the redirect URI, or
  not matching what the Keycloak client registers.

`signIn()` now times out after five minutes with an explanatory message rather
than hanging indefinitely.

## Redirect URI

`io.docvault.flutter://oauth/callback`, registered natively on both platforms:

- Android — `manifestPlaceholders["appAuthRedirectScheme"]` in `app/build.gradle.kts`
- iOS — `CFBundleURLTypes` in `Info.plist`

Both must match `docvault-flutter` in `infra/terraform/20-realm/clients.tf`.

The React Native app deliberately uses a **different** scheme (`io.docvault.rn`)
and a different client. A custom scheme is claimed OS-wide, so two apps sharing
one is ambiguous — Android resolves it non-deterministically, iOS generally
favours whichever was installed last — and an OAuth redirect can be delivered to
the wrong app.

A
custom scheme can be claimed by another app on the device, which is why PKCE is
mandatory here; for production prefer App Links / Universal Links, which are
cryptographically bound to a domain you control.

## Tests

```bash
flutter test --dart-define-from-file=config/local.json
```

Covers role flattening (mirroring the server's claims transformation) and that the
`--dart-define-from-file` wiring actually reaches the compiled app — the config
test fails without the file, which is the point.

See [docs/05-integration-mobile.md](../../docs/05-integration-mobile.md) for the
design notes.
