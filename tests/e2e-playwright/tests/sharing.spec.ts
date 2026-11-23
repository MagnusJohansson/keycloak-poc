// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

import { expect, test, type Browser, type Page } from '@playwright/test';

/**
 * A private document, shared and unshared, seen by two people in two browsers.
 *
 * Requires the lab to be running:  make up && make seed && make api && make web
 * (the API with Sharing:Mode OpenFga, which `make api` sets).
 *
 * tools/assert-fga.py checks the decisions in depth. This shows them in the app:
 * carol, an administrator in the same tenant, does not see alice's private document
 * until alice shares it, and does not see it again after alice stops.
 */

const PASSWORD = process.env.SEED_PASSWORD ?? 'DocVaultLab!2026';

async function signedIn(browser: Browser, username: string): Promise<Page> {
  // A context each: two people, two sets of cookies, as in two browsers.
  const page = await (await browser.newContext()).newPage();
  await page.goto('/');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.fill('#username', username);
  await page.fill('#password', PASSWORD);
  await page.click('#kc-login');
  await expect(page).toHaveURL(/localhost:5173/);
  await expect(page.getByTestId('tenant')).toContainText('acme');
  return page;
}

test('alice shares a private document with carol, then stops', async ({ browser }) => {
  const title = `E2E private ${Date.now()}`;
  const alice = await signedIn(browser, 'alice');
  const carol = await signedIn(browser, 'carol');

  await alice.getByPlaceholder('New document title').fill(title);
  await alice.getByLabel('Visibility').selectOption('private');
  await alice.getByRole('button', { name: 'Create' }).click();
  const row = alice.getByTestId(`document-${title}`);
  await expect(row).toContainText('private');

  // Same tenant, the doc.admin role, and still not hers to see.
  await carol.reload();
  await expect(carol.getByTestId('documents')).toContainText('Acme Q3 Roadmap');
  await expect(carol.getByTestId('documents')).not.toContainText(title);

  await row.getByRole('button', { name: 'Share' }).click();
  await row.getByLabel('Share with whom').fill('carol');
  await row.getByRole('button', { name: 'Share', exact: true }).last().click();
  await expect(row.getByTestId('shares')).toContainText('carol');

  await carol.reload();
  await expect(carol.getByTestId('documents')).toContainText(title);
  // Seen, not owned: no Share for her.
  await expect(carol.getByTestId(`document-${title}`).getByRole('button', { name: 'Share' })).toHaveCount(0);

  await row.getByTestId('shares').getByRole('button', { name: 'Remove' }).click();
  await expect(row.getByTestId('shares')).toContainText('Not shared with anyone');

  await carol.reload();
  await expect(carol.getByTestId('documents')).toContainText('Acme Q3 Roadmap');
  await expect(carol.getByTestId('documents')).not.toContainText(title);

  await row.getByRole('button', { name: 'Delete' }).click();
  await expect(alice.getByTestId('documents')).not.toContainText(title);
});
