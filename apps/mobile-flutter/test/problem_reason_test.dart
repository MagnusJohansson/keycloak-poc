// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

import 'package:flutter_test/flutter_test.dart';
import 'package:docvault_mobile/main.dart';

/// A 403 is not always a missing role: "no tenant" and "ambiguous tenant" are 403s whose
/// problem body says which. The app shows that reason instead of guessing.
void main() {
  test('prefers detail over title', () {
    expect(
      problemReason('{"title":"Ambiguous tenant","status":403,"detail":"Your account belongs to more than one tenant."}'),
      'Your account belongs to more than one tenant.',
    );
  });

  test('falls back to title', () {
    expect(problemReason('{"title":"No tenant","status":403}'), 'No tenant');
  });

  test('returns null for the empty body of a role check 403', () {
    expect(problemReason(''), isNull);
  });

  test('returns null for a body that is not a problem document', () {
    expect(problemReason('[1,2]'), isNull);
    expect(problemReason('<html>'), isNull);
  });
}
