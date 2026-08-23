# DocVault — React Native client

Runnable-lite: signs in, shows its claims, calls one protected endpoint.

## The one thing that differs from the web

The redirect is a **custom URI scheme**, and registering it is a *native* change
on each platform. Miss it and sign-in silently never returns.

**Android** — `android/app/build.gradle`:

```gradle
defaultConfig {
    manifestPlaceholders = [appAuthRedirectScheme: 'io.docvault.rn']
}
```

**iOS** — `ios/DocVault/Info.plist`:

```xml
<key>CFBundleURLTypes</key>
<array>
  <dict>
    <key>CFBundleURLSchemes</key>
    <array><string>io.docvault.rn</string></array>
  </dict>
</array>
```

Both must match the redirect URI registered on the `docvault-reactnative` client in
`infra/terraform/20-realm/clients.tf`.

## Reaching the local lab from a device

| Target | Issuer |
|---|---|
| Android emulator | `http://10.0.2.2:8080/realms/docvault` |
| iOS simulator | `http://localhost:8080/realms/docvault` |
| Physical device | `http://<your-LAN-ip>:8080/realms/docvault` |

`localhost` inside an emulator is the emulator itself, not your machine — the
most common reason the app cannot reach Keycloak.

> Plain HTTP needs an ATS exception on iOS and `usesCleartextTraffic` on Android.
> Both are for the local lab only; the Azure deployment is HTTPS, so neither is
> needed once you point at the cloud.

## Scaffolding

This directory holds the auth logic only. To get a runnable app:

```bash
npx @react-native-community/cli init DocVault --directory .
npm install react-native-app-auth react-native-keychain
```

then apply the two native changes above and import from `src/auth.ts`.
