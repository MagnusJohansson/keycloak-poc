// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

const fs = require('node:fs');
const path = require('node:path');

/**
 * Settings for the desktop client: which Keycloak, which API, which client id.
 *
 * Sources, later overriding earlier:
 *   1. built-in defaults - the local Docker lab
 *   2. config.json beside the app
 *   3. DOCVAULT_-prefixed environment variables
 *
 * A file is the primary mechanism because a desktop app launched from Finder,
 * Explorer or a dock icon inherits no useful environment. The variables remain
 * for CI and for running two instances against different realms.
 *
 * NOTE: this app cannot share apps/web-react/.env. Vite only exposes VITE_*
 * variables, the names differ, and the two use DIFFERENT Keycloak clients
 * (docvault-desktop vs docvault-web-react) so a shared clientId would be wrong.
 */

const DEFAULTS = {
  issuer: 'http://localhost:8080/realms/docvault',
  apiBaseUrl: 'http://localhost:5001',
  clientId: 'docvault-desktop',
  scope: 'openid profile email',
  stepUpAcr: 'silver',
};

const ENV_KEYS = {
  issuer: 'DOCVAULT_ISSUER',
  apiBaseUrl: 'DOCVAULT_API_URL',
  clientId: 'DOCVAULT_CLIENT_ID',
  scope: 'DOCVAULT_SCOPE',
  stepUpAcr: 'DOCVAULT_STEP_UP_ACR',
};

function readFile(configPath) {
  try {
    const raw = fs.readFileSync(configPath, 'utf8');
    // Keys beginning with "//" are comments; JSON has none of its own.
    return Object.fromEntries(
      Object.entries(JSON.parse(raw)).filter(([key]) => !key.startsWith('//')),
    );
  } catch (error) {
    if (error.code === 'ENOENT') return {};
    throw new Error(`config.json is not valid JSON: ${error.message}`);
  }
}

/**
 * Fails fast on configuration that cannot work, rather than surfacing it later as
 * an opaque browser error.
 */
function validate(settings) {
  for (const [key, value] of [['issuer', settings.issuer], ['apiBaseUrl', settings.apiBaseUrl]]) {
    let url;
    try {
      url = new URL(value);
    } catch {
      throw new Error(`${key} '${value}' is not an absolute URL.`);
    }
    if (url.protocol !== 'http:' && url.protocol !== 'https:') {
      throw new Error(`${key} '${value}' must be http or https.`);
    }
  }

  // A realm issuer always contains /realms/<name>. Pointing at the host root is a
  // common slip and produces a 404 on discovery that reads like the server is down.
  if (!new URL(settings.issuer).pathname.includes('/realms/')) {
    throw new Error(
      `issuer '${settings.issuer}' does not look like a realm issuer - it should include ` +
        '/realms/<realm>, e.g. https://your-keycloak/realms/docvault.',
    );
  }

  if (!settings.clientId) {
    throw new Error('clientId must be set.');
  }

  return settings;
}

function loadConfig(baseDir = path.join(__dirname, '..')) {
  const fromFile = readFile(path.join(baseDir, 'config.json'));

  const fromEnv = Object.fromEntries(
    Object.entries(ENV_KEYS)
      .map(([key, envName]) => [key, process.env[envName]])
      .filter(([, value]) => value !== undefined && value !== ''),
  );

  return validate({ ...DEFAULTS, ...fromFile, ...fromEnv });
}

module.exports = { loadConfig, DEFAULTS, ENV_KEYS };
