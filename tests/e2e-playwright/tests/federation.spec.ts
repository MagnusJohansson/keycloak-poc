// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

import { expect, test } from '@playwright/test';

/**
 * People who are not DocVault's own, in a real browser: one from the LDAP
 * directory, one from the legacy SAML provider.
 *
 * Requires the lab to be running:  make up && make seed && make api && make web
 *
 * tools/assert-ldap.py and tools/assert-saml.py check these sign-ins in depth,
 * but they imitate a browser. The SAML one in particular is a chain of pages
 * that post themselves on with JavaScript, across two realms that share cookie
 * names on one host. Only a browser shows that the chain holds.
 */

const PASSWORD = process.env.SEED_PASSWORD ?? 'DocVaultLab!2026';

test('erin, from the LDAP directory, signs in and sees Acme documents', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Sign in' }).click();

  await expect(page).toHaveURL(/\/realms\/docvault\/protocol\/openid-connect\/auth/);
  await page.fill('#username', 'erin');
  await page.fill('#password', PASSWORD);
  await page.click('#kc-login');

  await expect(page).toHaveURL(/localhost:5173/);
  await expect(page.getByTestId('tenant')).toContainText('acme');
  await expect(page.getByTestId('documents')).toContainText('Acme Q3 Roadmap');
});

test('heidi signs in at the legacy SAML provider and sees Acme documents', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Sign in' }).click();

  // DocVault's login page offers the legacy provider.
  await expect(page).toHaveURL(/\/realms\/docvault\/protocol\/openid-connect\/auth/);
  await page.getByRole('link', { name: 'Legacy sign-in (SAML)' }).click();

  // Now at the legacy provider: another realm, reached with a signed SAML request.
  await expect(page).toHaveURL(/\/realms\/legacy-idp\//);
  await page.fill('#username', 'heidi');
  await page.fill('#password', PASSWORD);
  await page.click('#kc-login');

  // Back through DocVault's Keycloak, which turns the assertion into its own token.
  await expect(page).toHaveURL(/localhost:5173/);
  await expect(page.getByTestId('tenant')).toContainText('acme');

  const documents = page.getByTestId('documents');
  await expect(documents).toContainText('Acme Q3 Roadmap');
  await expect(documents).not.toContainText('Globex Supplier List');
});

test('alice signs in to the legacy SAML application through Keycloak', async ({ page }) => {
  // The application has no login page of its own: it sends her to Keycloak
  // with a signed SAML request, and Keycloak posts a signed response back.
  await page.goto('http://localhost:5003/');

  await expect(page).toHaveURL(/\/realms\/docvault\//);
  await page.fill('#username', 'alice');
  await page.fill('#password', PASSWORD);
  await page.click('#kc-login');

  // Back at the application. That it got here at all shows the browser kept
  // the application's sign-in cookie across the round trip to another site.
  await expect(page).toHaveURL('http://localhost:5003/');
  await expect(page.getByTestId('username')).toHaveText('alice');
  await expect(page.getByTestId('tenant')).toHaveText('acme');

  await page.getByRole('button', { name: 'Sign out of Reports' }).click();
  await page.goto('http://localhost:5003/me');
  // Signed out of the application, still signed in at Keycloak: no password this time.
  await expect(page).toHaveURL('http://localhost:5003/me');
  await expect(page.locator('body')).toContainText('"username":"alice"');
});
