import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  // Serial: these tests share one Keycloak SSO session and one in-memory
  // document store, so parallel runs would interfere with each other.
  workers: 1,
  fullyParallel: false,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: process.env.WEB_BASE_URL ?? 'http://localhost:5173',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
