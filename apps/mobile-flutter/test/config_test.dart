// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

import 'package:flutter_test/flutter_test.dart';

/// Proves the --dart-define-from-file mechanism actually reaches the compiled app.
///
/// Flutter configuration is COMPILE-TIME: String.fromEnvironment is resolved when
/// the app is built, so a runtime environment variable has no effect. That is the
/// single most surprising thing about configuring a Flutter app, and this test
/// fails loudly if the wiring stops working.
///
/// Run with:
///   flutter test --dart-define-from-file=config/local.json
void main() {
  const issuer = String.fromEnvironment('ISSUER', defaultValue: '(unset)');
  const apiBaseUrl = String.fromEnvironment('API_BASE_URL', defaultValue: '(unset)');
  const clientId = String.fromEnvironment('CLIENT_ID', defaultValue: '(unset)');

  test('values from --dart-define-from-file are compiled in', () {
    expect(issuer, isNot('(unset)'),
        reason: 'Run with --dart-define-from-file=config/local.json');
    expect(Uri.parse(issuer).isAbsolute, isTrue);
  });

  test('issuer is a realm URL, not just a host', () {
    // Pointing at the host root is a common slip; discovery then 404s in a way
    // that reads like the server being down.
    expect(issuer, contains('/realms/'));
  });

  test('api base url is absolute', () {
    expect(Uri.parse(apiBaseUrl).isAbsolute, isTrue);
  });

  test('client id matches a client registered in the realm', () {
    // See infra/terraform/20-realm/clients.tf. Flutter and React Native have
    // SEPARATE clients and separate URI schemes, so each can be revoked and
    // audited on its own and neither can intercept the other's redirect.
    expect(clientId, 'docvault-flutter');
  });
}
