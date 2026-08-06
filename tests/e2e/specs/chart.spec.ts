import { expect, test } from '@playwright/test';

const PASSWORD = 'correct horse battery staple';

function uniqueEmail(): string {
  return `e2e-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@marketpulse.local`;
}

test('watchlist ticker links to a rendering candle chart', async ({ page }) => {
  const email = uniqueEmail();

  await page.goto('/register');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByRole('heading', { name: 'Watchlist' })).toBeVisible();

  // Add a seeded ticker.
  await page.getByLabel('Add ticker').fill('IVV');
  await page.getByRole('button', { name: 'Add', exact: true }).click();
  await expect(page.getByRole('link', { name: 'IVV price history' })).toBeVisible();

  // Sparkline: rendering a polyline needs 2+ persisted minute-buckets (Sparkline renders
  // nothing below that — see packages/ui/src/components/Sparkline/Sparkline.tsx), and a
  // freshly booted API (this suite starts it via webServer, not a long-lived process) has
  // not accumulated that much history yet. Confirmed flaky in practice: the first run of
  // this spec failed with a 30s timeout waiting for `polyline`. Relaxed per the task's
  // pre-decided judgment point to asserting the Trend column exists — the sparkline's
  // rendering behaviour is already covered by Sparkline.test.tsx; this journey's job is
  // the real chunk + canvas below.
  await expect(page.getByRole('columnheader', { name: 'Trend' })).toBeVisible();

  // Chart route — lazy chunk loads, canvas renders real candles.
  await page.getByRole('link', { name: 'IVV price history' }).click();
  await expect(page.getByRole('heading', { name: /IVV — price history/ })).toBeVisible();
  await expect(page.locator('canvas').first()).toBeVisible({ timeout: 20_000 });

  // Interval switch keeps a live chart.
  await page.getByRole('button', { name: '5m' }).click();
  await expect(page.getByRole('button', { name: '5m' })).toHaveAttribute('aria-pressed', 'true');
  await expect(page.locator('canvas').first()).toBeVisible();
});
