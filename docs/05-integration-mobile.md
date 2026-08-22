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
manifestPlaceholders["appAuthRedirectScheme"] = "io.docvault.app"
```

**iOS** — `ios/Runner/Info.plist`:

```xml
<key>CFBundleURLTypes</key>
<array><dict>
  <key>CFBundleURLSchemes</key>
  <array><string>io.docvault.app</string></array>
</dict></array>
```

Both must match `docvault-mobile`'s registered redirect URI in
`infra/terraform/20-realm/clients.tf`.

> A custom scheme can be claimed by *another* app on the device. That is exactly
> why PKCE is non-negotiable here: an intercepted authorization code is useless
> without the verifier. For production, prefer **App Links / Universal Links**,
> which are cryptographically bound to a domain you control.

## Reaching the local lab

| Target | Issuer |
|---|---|
| Android emulator | `http://10.0.2.2:8080/realms/docvault` |
| iOS simulator | `http://localhost:8080/realms/docvault` |
| Physical device | `http://<your-LAN-ip>:8080/realms/docvault` |

`localhost` inside an Android emulator is the emulator itself — the most common
reason the app "cannot reach Keycloak". Plain HTTP also needs an ATS exception on
iOS and `usesCleartextTraffic` on Android; both are local-lab-only, since a
Azure deployment is HTTPS.

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

## Running

```bash
cd apps/mobile-flutter
flutter run --dart-define=ISSUER=http://10.0.2.2:8080/realms/docvault
```

React Native ships the auth module (`src/auth.ts`) rather than a full app; its
README has the scaffolding steps.

---

Next: [7. Desktop integration](06-integration-desktop.md).
