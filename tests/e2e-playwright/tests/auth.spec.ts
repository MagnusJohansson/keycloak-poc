// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

import { expect, test, type Page } from '@playwright/test';

/**
 * End-to-end through a real browser against a real Keycloak.
 *
 * Requires the lab to be running:  make up && make seed && make api && make web
 *
 * These cover what unit tests structurally cannot: the redirect to Keycloak, the
 * authorization-code exchange, and the cookie/session behaviour of the browser.
 */

const PASSWORD = process.env.SEED_PASSWORD ?? 'DocVaultLab!2026';

async function signIn(page: Page, username: string) {
  await page.goto('/');
  await page.getByRole('button', { name: 'Sign in' }).click();

  // We are now on Keycloak's login page, a different origin.
  await expect(page).toHaveURL(/\/realms\/docvault\/protocol\/openid-connect\/auth/);
  await page.fill('#username', username);
  await page.fill('#password', PASSWORD);
  await page.click('#kc-login');

  await expect(page).toHaveURL(/localhost:5173/);
}

test('alice signs in and sees only Acme documents', async ({ page }) => {
  await signIn(page, 'alice');

  await expect(page.getByTestId('tenant')).toContainText('acme');

  const documents = page.getByTestId('documents');
  await expect(documents).toContainText('Acme Q3 Roadmap');
  await expect(documents).not.toContainText('Globex Supplier List');
});

test('an editor sees the create form; the API accepts the write', async ({ page }) => {
  await signIn(page, 'alice');

  await page.getByPlaceholder('New document title').fill('E2E created document');
  await page.getByRole('button', { name: 'Create' }).click();

  await expect(page.getByTestId('documents')).toContainText('E2E created document');
});

test('tenant isolation: bob cannot see Acme documents', async ({ page }) => {
  await signIn(page, 'bob');

  await expect(page.getByTestId('tenant')).toContainText('globex');

  const documents = page.getByTestId('documents');
  await expect(documents).toContainText('Globex Supplier List');
  await expect(documents).not.toContainText('Acme Q3 Roadmap');
});

test('a reader does not get the create form', async ({ page }) => {
  await signIn(page, 'bob');

  // The guard renders an explanation rather than redirecting - an
  // authenticated-but-unauthorized user must not be sent back to login.
  await expect(page.getByPlaceholder('New document title')).toHaveCount(0);
  await expect(page.getByTestId('role-denied')).toBeVisible();
});

test('the token inspector shows the claims the API relies on', async ({ page }) => {
  await signIn(page, 'alice');

  const inspector = page.getByTestId('inspector');
  await expect(inspector).toContainText('docvault-api');   // the audience mapper worked
  await expect(inspector).toContainText('/acme/engineering'); // the groups mapper worked
});

test('classified access is refused until the user steps up', async ({ page }) => {
  await signIn(page, 'carol');

  await page.getByRole('link', { name: 'Classified' }).click();
  await page.getByRole('button', { name: 'Load classified documents' }).click();

  // RFC 9470: the API tells the client how to recover, and the SPA offers it.
  await expect(page.getByTestId('stepup-challenge')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Verify with OTP' })).toBeVisible();

  // Completing OTP needs a TOTP secret, so the enrolment half is left manual;
  // docs/use-cases/uc3-step-up-mfa.md walks through it. What is asserted here is
  // the part that regresses silently: that the challenge is issued at all.
});

test('a user with no roles is told why, and is not bounced to login', async ({ page }) => {
  await signIn(page, 'dave');

  // 403, not a login loop.
  await expect(page).toHaveURL(/localhost:5173/);
  // dave has no roles, so the RBAC guard explains rather than redirecting.
  await expect(page.getByTestId('role-denied')).toBeVisible();
});
