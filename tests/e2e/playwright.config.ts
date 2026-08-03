import { defineConfig } from '@playwright/test';

const API_URL = 'http://localhost:5100';
const APP_URL = 'http://localhost:4173';

export default defineConfig({
  testDir: './specs',
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
    },
    {
      command: 'pnpm --filter @marketpulse/dashboard preview --port 4173 --strictPort',
      url: APP_URL,
      reuseExistingServer: !process.env['CI'],
      timeout: 120_000,
    },
  ],
});
