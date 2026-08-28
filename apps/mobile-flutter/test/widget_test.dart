// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

import 'package:flutter_test/flutter_test.dart';
import 'package:docvault_mobile/auth/docvault_auth.dart';

void main() {
  group('rolesFrom', () {
    test('merges realm roles and this API\'s client roles', () {
      final roles = DocVaultAuth.rolesFrom({
        'realm_access': {'roles': ['platform-admin']},
        'resource_access': {
          'docvault-api': {'roles': ['doc.editor']},
        },
      });

      expect(roles, containsAll(<String>['platform-admin', 'doc.editor']));
    });

    test('ignores roles belonging to other clients', () {
      // Mirrors the server-side rule: a role granted on a different client must
      // not grant anything here.
      final roles = DocVaultAuth.rolesFrom({
        'resource_access': {
          'some-other-app': {'roles': ['doc.admin']},
        },
      });

      expect(roles, isEmpty);
    });

    test('drops the default-roles composite', () {
      final roles = DocVaultAuth.rolesFrom({
        'realm_access': {'roles': ['default-roles-docvault', 'platform-admin']},
      });

      expect(roles, <String>['platform-admin']);
    });
  });
}
