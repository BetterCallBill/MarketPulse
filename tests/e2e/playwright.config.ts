import { defineConfig } from '@playwright/test';

const API_URL = 'http://localhost:5100';
const APP_URL = 'http://localhost:4173';

export default defineConfig({
  testDir: './specs',
  globalSetup: './global-setup.ts',
  globalTeardown: './global-teardown.ts',
  timeout: 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  retries: process.env['CI'] ? 1 : 0,
  reporter: process.env['CI'] ? 'list' : 'html',

  use: {
    baseURL: APP_URL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },

  // Both servers must be up before any spec runs. /health exists precisely so Playwright
  // has an unauthenticated 200 to poll — /api/v1/auth/me answers 401 by design.
  webServer: [
    {
      command: 'dotnet run --project ../../src/MarketPulse.Api --launch-profile http',
      url: `${API_URL}/health`,
      reuseExistingServer: !process.env['CI'],
      timeout: 120_000,
      stdout: 'pipe',
      stderr: 'pipe',
      // The per-IP "auth" fixed window (register/login/refresh, 10/min by default) is sized
      // for a real client, not a suite that runs several journeys' worth of anonymous page
      // loads back to back from one IP within the same window — every unauthenticated mount
      // fires a spurious refresh attempt (401 on /me), which alone eats most of the budget
      // before any spec calls register or login. Raised here, not in appsettings, so
      // production and dev defaults are untouched and only the e2e process gets headroom.
      env: { Auth__LoginRequestsPerMinute: '1000' },
    },
    {
      command: 'pnpm --filter @marketpulse/dashboard preview --port 4173 --strictPort',
      url: APP_URL,
      reuseExistingServer: !process.env['CI'],
      timeout: 120_000,
    },
  ],
});
