# 6. Mobile integration (Flutter & React Native)

Source: `apps/mobile-flutter`, `apps/mobile-react-native`.

## Two rules that differ from the web

### 1. Use the system browser, never a WebView

Both clients open `ASWebAuthenticationSession` (iOS) / Chrome Custom Tabs
(Android) via AppAuth.

An in-app WebView is controlled by your app, which means it *can* read the
password as the user types it. It also cannot share the system SSO session, and
it hides the real URL bar so the user cannot verify who is asking. RFC 8252 says
use the system browser; most identity providers now reject WebView traffic.

### 2. Tokens go in the platform keystore

Keychain on iOS, hardware-backed Keystore on Android. **Not** SharedPreferences or
UserDefaults — those are plain files, readable on a rooted or jailbroken device.

```dart
static const FlutterSecureStorage _storage = FlutterSecureStorage(
  aOptions: AndroidOptions(),   // v11 default: AES-GCM under a KeyStore-wrapped RSA key
  iOptions: IOSOptions(accessibility: KeychainAccessibility.first_unlock),
);
```

> `encryptedSharedPreferences: true` was **removed** in `flutter_secure_storage`
> v11 — that behaviour is now the default. Older tutorials still pass it and will
> not compile.

## Redirect URIs are a native change

The redirect is a custom URI scheme, and registering it is platform-native work.
Forgetting it is the usual reason sign-in appears to hang forever — the browser
completes, and nothing routes back.

**Android** — `android/app/build.gradle.kts`:

```kotlin
manifestPlaceholders["appAuthRedirectScheme"] = "io.docvault.flutter"
```

**iOS** — `ios/Runner/Info.plist`:

```xml
<key>CFBundleURLTypes</key>
<array><dict>
  <key>CFBundleURLSchemes</key>
  <array><string>io.docvault.flutter</string></array>
</dict></array>
```

Both must match `docvault-flutter`'s registered redirect URI in
`infra/terraform/20-realm/clients.tf`.

> **The two mobile apps use different schemes on purpose** —
> `io.docvault.flutter://` and `io.docvault.rn://` — with a Keycloak client each.
> A custom scheme is claimed OS-wide, so two apps registering the same one is
> ambiguous: Android resolves it non-deterministically and iOS generally favours
> whichever was installed last, so a redirect can reach the wrong app.
>
> More generally, a custom scheme can be claimed by *another* app on the device
> entirely. That is exactly why PKCE is non-negotiable here: an intercepted authorization code is useless
> without the verifier. For production, prefer **App Links / Universal Links**,
> which are cryptographically bound to a domain you control.

## Reaching the local lab

| Target | Issuer |
|---|---|
| iOS simulator | `http://localhost:8080/realms/docvault` |
| Android emulator | `http://localhost:8080/realms/docvault`, after `make android-reverse` |
| Physical device | `http://localhost:8080/realms/docvault`, after `make android-reverse` over USB |

`localhost` inside an Android emulator is the emulator itself, which is why the
forwarding is needed. The tempting shortcut — pointing the emulator at `10.0.2.2`,
its alias for the host — **changes the issuer**, because Keycloak derives `iss`
from the request host. The API trusts exactly one issuer, so sign-in then succeeds
and every API call returns 401. Match the issuer instead of widening what the API
accepts.

Plain HTTP also needs an exception: `NSExceptionDomains` on iOS, and
`network_security_config.xml` on Android — **not** `usesCleartextTraffic="true"`,
which disables the protection for every host the app contacts. Both are
local-lab-only; the Azure deployment is HTTPS and needs neither.

## Refresh handling

```dart
if (accessToken != null && !JwtDecoder.isExpired(accessToken)) return accessToken;
// otherwise refresh; on failure, sign out rather than retry-loop
```

The realm has refresh-token reuse detection on, so a replayed or revoked token
ends the session. Both clients clear local state and require a fresh sign-in
instead of retrying — a retry loop against a dead session just burns battery and
confuses the user.

## Roles

`DocVaultAuth.rolesFrom()` mirrors the server's claims transformation exactly:
realm roles plus *this API's* client roles, ignoring other clients' roles and the
`default-roles-*` composite. Unit-tested in `test/widget_test.dart` — including
the case that a role granted on a different client grants nothing here.

## Configuration is compile-time

`String.fromEnvironment` is resolved when the app is **built**, so a runtime
environment variable does nothing. Values come from a JSON file:

```bash
flutter run --dart-define-from-file=config/local.json    # the local lab, any target
flutter run --dart-define-from-file=config/azure.json    # your deployment
```

Changing a value needs a **rebuild**, not a restart. This differs from every other
client here: the desktop apps read a file at startup and can be edited in place.

## Cleartext HTTP for the local lab

Both platforms block plain HTTP by default, so the local lab needs an exception -
scoped to loopback and the emulator host alias, never opened globally:

- iOS: `NSExceptionDomains` for `localhost` and `10.0.2.2`, **not** `NSAllowsArbitraryLoads`
- Android: `network_security_config.xml` limited to the same two, **not** `usesCleartextTraffic="true"`

Azure is HTTPS and needs neither, which makes it the easier target for mobile
testing.

## Running

```bash
cd apps/mobile-flutter
open -a Simulator && flutter run --dart-define-from-file=config/local.json
```

On Android, forward the ports first so the emulator sees the same `localhost` the
API validates tokens against:

```bash
make android-reverse
flutter run --dart-define-from-file=config/local.json
```

**Do not point the emulator at `10.0.2.2` instead.** Keycloak takes `iss` from the
request host, so that mints tokens carrying a different issuer than the API trusts:
sign-in succeeds and then every API call returns 401 (`IDX10205`). Matching the
issuer beats widening what the API accepts.

React Native ships the auth module (`src/auth.ts`) rather than a full app; its
README has the scaffolding steps.

---

Next: [7. Desktop integration](06-integration-desktop.md).
