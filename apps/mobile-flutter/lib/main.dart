// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:http/http.dart' as http;

import 'auth/docvault_auth.dart';

/// Configured at build time so the same binary can target the local lab or a
/// Azure deployment:
///
///   `flutter run --dart-define=ISSUER=... --dart-define=API_BASE_URL=...`
///
/// where ISSUER is your Keycloak realm URL.
///
/// The Android emulator reaches the host machine at 10.0.2.2, not localhost -
/// localhost inside the emulator is the emulator itself.
const issuer = String.fromEnvironment('ISSUER', defaultValue: 'http://10.0.2.2:8080/realms/docvault');
const apiBaseUrl = String.fromEnvironment('API_BASE_URL', defaultValue: 'http://10.0.2.2:5001');
const clientId = String.fromEnvironment('CLIENT_ID', defaultValue: 'docvault-flutter');
const redirectUri = 'io.docvault.flutter://oauth/callback';

void main() => runApp(const DocVaultApp());

class DocVaultApp extends StatelessWidget {
  const DocVaultApp({super.key});

  @override
  Widget build(BuildContext context) => MaterialApp(
        title: 'DocVault',
        theme: ThemeData.dark(useMaterial3: true),
        home: const HomePage(),
      );
}

class HomePage extends StatefulWidget {
  const HomePage({super.key});

  @override
  State<HomePage> createState() => _HomePageState();
}

class _HomePageState extends State<HomePage> {
  final _auth = DocVaultAuth(issuer: issuer, clientId: clientId, redirectUri: redirectUri);

  Map<String, dynamic>? _claims;
  String? _status;
  List<dynamic> _documents = const [];

  Future<void> _run(Future<void> Function() action) async {
    setState(() => _status = null);
    try {
      await action();
    } catch (e) {
      setState(() => _status = '$e');
    }
  }

  Future<void> _signIn() => _run(() async {
        final claims = await _auth.signIn();
        setState(() => _claims = claims);
      });

  Future<void> _stepUp() => _run(() async {
        final claims = await _auth.stepUp('silver');
        setState(() => _claims = claims);
      });

  Future<void> _loadDocuments() => _run(() async {
        final token = await _auth.currentAccessToken();
        if (token == null) {
          setState(() => _status = 'Not signed in.');
          return;
        }

        final response = await http.get(
          Uri.parse('$apiBaseUrl/documents'),
          headers: {'Authorization': 'Bearer $token'},
        );

        // Same step-up contract as the web client: the API says what is missing. RFC 9470
        // sends it as a 401, so the header, not the status, decides.
        final challenge = response.headers['www-authenticate'] ?? '';
        if (challenge.contains('insufficient_user_authentication')) {
          setState(() => _status = 'Step-up required - tap "Step up (OTP)".');
          return;
        }

        if (response.statusCode == 403) {
          // Not always a missing role: "no tenant" and "ambiguous tenant" are 403s too, and
          // their problem body says which. Pass the server's reason on; a role check's 403
          // has no body.
          setState(() => _status = 'Forbidden: ${problemReason(response.body) ?? 'you may not access this.'}');
          return;
        }

        if (response.statusCode != 200) {
          setState(() => _status = 'API returned ${response.statusCode}');
          return;
        }

        setState(() => _documents = (jsonDecode(response.body)['documents'] as List?) ?? const []);
      });

  Future<void> _signOut() => _run(() async {
        await _auth.signOut();
        setState(() {
          _claims = null;
          _documents = const [];
        });
      });

  @override
  Widget build(BuildContext context) {
    final roles = _claims == null ? <String>[] : DocVaultAuth.rolesFrom(_claims!);

    return Scaffold(
      appBar: AppBar(title: const Text('DocVault')),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          Wrap(spacing: 8, runSpacing: 8, children: [
            FilledButton(onPressed: _signIn, child: const Text('Sign in')),
            OutlinedButton(onPressed: _loadDocuments, child: const Text('Load documents')),
            OutlinedButton(onPressed: _stepUp, child: const Text('Step up (OTP)')),
            TextButton(onPressed: _signOut, child: const Text('Sign out')),
          ]),
          if (_status != null) ...[
            const SizedBox(height: 16),
            Text(_status!, style: TextStyle(color: Theme.of(context).colorScheme.error)),
          ],
          if (_claims != null) ...[
            const SizedBox(height: 16),
            Text('Signed in as ${_claims!['preferred_username']}'),
            Text('acr: ${_claims!['acr']}   roles: ${roles.join(', ')}'),
          ],
          if (_documents.isNotEmpty) ...[
            const SizedBox(height: 16),
            const Text('Documents', style: TextStyle(fontWeight: FontWeight.bold)),
            for (final doc in _documents) ListTile(dense: true, title: Text('${doc['title']}')),
          ],
          if (_claims != null) ...[
            const SizedBox(height: 16),
            const Text('Access token claims', style: TextStyle(fontWeight: FontWeight.bold)),
            SelectableText(DocVaultAuth.pretty(_claims!), style: const TextStyle(fontFamily: 'monospace', fontSize: 11)),
          ],
        ],
      ),
    );
  }
}

/// The reason in an RFC 9457 problem body (`detail`, else `title`), or null when the body is
/// empty or not a problem document — as for a role check's 403, which carries no body.
String? problemReason(String body) {
  try {
    final json = jsonDecode(body);
    if (json is! Map) return null;
    // Type-checked rather than cast: a non-string field would throw TypeError, which the
    // FormatException handler below does not catch.
    for (final key in const ['detail', 'title']) {
      final value = json[key];
      if (value is String && value.isNotEmpty) return value;
    }
    return null;
  } on FormatException {
    return null;
  }
}
