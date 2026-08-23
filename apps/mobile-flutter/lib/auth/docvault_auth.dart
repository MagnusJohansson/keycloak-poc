import 'dart:async';
import 'dart:convert';

import 'package:flutter_appauth/flutter_appauth.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:jwt_decoder/jwt_decoder.dart';

/// OIDC for a native app.
///
/// Two things differ from the browser case and both matter:
///
/// 1. The redirect goes to a custom URI scheme the OS routes back to this app,
///    not to an https URL. Another app on the device can register the same
///    scheme, so PKCE is not optional here - it is what stops an interceptor
///    redeeming a stolen authorization code. flutter_appauth always sends it.
///
/// 2. Tokens are persisted to the platform keystore (Keychain on iOS, Keystore
///    on Android) rather than SharedPreferences/UserDefaults, which are plain
///    files readable on a rooted or jailbroken device.
class DocVaultAuth {
  DocVaultAuth({
    required this.issuer,
    required this.clientId,
    required this.redirectUri,
  });

  /// e.g. `http://10.0.2.2:8080/realms/docvault` (Android emulator -> host)
  /// or   `https://<your-keycloak-host>/realms/docvault`
  final String issuer;
  final String clientId;
  final String redirectUri;

  final FlutterAppAuth _appAuth = const FlutterAppAuth();

  // iOS: Keychain, with first_unlock accessibility so a background token
  //      refresh still works after the device has been unlocked once.
  // Android: the default AndroidOptions in flutter_secure_storage 11 already
  //      encrypts with AES-GCM under an RSA key wrapped by the hardware
  //      KeyStore. (The older `encryptedSharedPreferences: true` flag was
  //      removed in v11 - that behaviour is now the default.)
  static const FlutterSecureStorage _storage = FlutterSecureStorage(
    aOptions: AndroidOptions(),
    iOptions: IOSOptions(accessibility: KeychainAccessibility.first_unlock),
  );

  static const _accessTokenKey = 'docvault.access_token';
  static const _refreshTokenKey = 'docvault.refresh_token';

  /// Opens the system browser (ASWebAuthenticationSession / Custom Tabs).
  ///
  /// Deliberately NOT a WebView: a WebView is controlled by this app, so it can
  /// read the user's credentials, cannot share the SSO session, and is rejected
  /// by most identity providers for exactly those reasons.
  Future<Map<String, dynamic>?> signIn() async {
    // Bounded, because the failure mode here is a HANG, not an exception.
    //
    // If the redirect cannot be matched to the pending request - a task-affinity
    // mismatch, a scheme registered wrongly, the activity being destroyed - AppAuth
    // logs "No stored state - unable to handle response" to logcat and the Future
    // simply never completes. Without a timeout the UI sits there with no error at
    // all, which is a genuinely baffling thing to debug.
    final result = await _appAuth
        .authorizeAndExchangeCode(
      AuthorizationTokenRequest(
        clientId,
        redirectUri,
        // Endpoints are discovered from /.well-known/openid-configuration, so
        // moving from the local Keycloak to Azure changes only this one string.
        discoveryUrl: '$issuer/.well-known/openid-configuration',
        scopes: const ['openid', 'profile', 'email'],
      ),
    )
        .timeout(
      const Duration(minutes: 5),
      onTimeout: () => throw TimeoutException(
        'Sign-in did not complete. If the browser returned but nothing happened, '
        'check logcat for "No stored state - unable to handle response" - that means '
        'the redirect could not be matched to the pending request.',
      ),
    );

    await _persist(result);
    return result.accessToken == null ? null : JwtDecoder.decode(result.accessToken!);
  }

  /// Requests a higher authentication level (step-up, use-case 3).
  ///
  /// `acr_values` is passed as an additional authorization parameter; Keycloak
  /// maps it to a Level of Authentication and demands OTP. `prompt=login`
  /// prevents the existing SSO session from satisfying the request silently.
  Future<Map<String, dynamic>?> stepUp(String acr) async {
    final result = await _appAuth.authorizeAndExchangeCode(
      AuthorizationTokenRequest(
        clientId,
        redirectUri,
        discoveryUrl: '$issuer/.well-known/openid-configuration',
        scopes: const ['openid', 'profile', 'email'],
        promptValues: const ['login'],
        additionalParameters: {'acr_values': acr},
      ),
    );

    await _persist(result);
    return result.accessToken == null ? null : JwtDecoder.decode(result.accessToken!);
  }

  /// Returns a valid access token, refreshing it if it has expired.
  Future<String?> currentAccessToken() async {
    final accessToken = await _storage.read(key: _accessTokenKey);

    if (accessToken != null && !JwtDecoder.isExpired(accessToken)) {
      return accessToken;
    }

    final refreshToken = await _storage.read(key: _refreshTokenKey);
    if (refreshToken == null) {
      return null;
    }

    try {
      final result = await _appAuth.token(TokenRequest(
        clientId,
        redirectUri,
        discoveryUrl: '$issuer/.well-known/openid-configuration',
        refreshToken: refreshToken,
        grantType: 'refresh_token',
      ));
      await _persist(result);
      return result.accessToken;
    } catch (_) {
      // The realm enables refresh-token reuse detection, so a replayed or
      // revoked token kills the session. Clear local state and make the user
      // sign in again rather than retrying into a loop.
      await signOut();
      return null;
    }
  }

  Future<void> signOut() async {
    await _storage.delete(key: _accessTokenKey);
    await _storage.delete(key: _refreshTokenKey);
  }

  Future<void> _persist(TokenResponse result) async {
    if (result.accessToken != null) {
      await _storage.write(key: _accessTokenKey, value: result.accessToken);
    }
    if (result.refreshToken != null) {
      await _storage.write(key: _refreshTokenKey, value: result.refreshToken);
    }
  }

  /// Roles the API will see, mirroring KeycloakClaimsTransformation server-side.
  static List<String> rolesFrom(Map<String, dynamic> claims, {String apiClientId = 'docvault-api'}) {
    final realm = (claims['realm_access']?['roles'] as List?)?.cast<String>() ?? const [];
    final client = (claims['resource_access']?[apiClientId]?['roles'] as List?)?.cast<String>() ?? const [];
    return [...realm, ...client].where((r) => !r.startsWith('default-roles-')).toList();
  }

  static String pretty(Map<String, dynamic> claims) =>
      const JsonEncoder.withIndent('  ').convert(claims);
}
