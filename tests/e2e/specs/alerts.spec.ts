import { expect, test } from '@playwright/test';

const PASSWORD = 'correct horse battery staple';

function uniqueEmail(): string {
  return `e2e-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@marketpulse.local`;
}

test('an alert set in the browser fires on a real tick and lands in the panel', async ({
  page,
}) => {
  const email = uniqueEmail();

  await page.goto('/register');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByRole('heading', { name: 'Watchlist' })).toBeVisible();

  await page.getByLabel('Add ticker').fill('IVV');
  await page.getByRole('button', { name: 'Add', exact: true }).click();

  // A live price proves the whole realtime path; it is also this test's worker-ready
  // gate — ticks flow through the same broker the worker consumes from.
  await expect(page.getByLabel('IVV price')).toHaveText(/^\$\d/, { timeout: 15_000 });

  // One-shot semantics make this deterministic: an Above rule below the current price
  // fires on the very next tick — no waiting for the random walk to wander anywhere.
  const priceText = await page.getByLabel('IVV price').textContent();
  const threshold = (Number(priceText!.replace(/[^0-9.]/g, '')) / 2).toFixed(2);

  await page.getByLabel('Alert direction for IVV').selectOption('Above');
  await page.getByLabel('Alert threshold for IVV').fill(threshold);
  await page.getByRole('button', { name: 'Set alert for IVV' }).click();

  // The worker evaluates the next tick, marks the rule, and the SignalR-invalidated
  // alerts query flips the row to Triggered without a refresh.
  await expect(page.getByText('Triggered')).toBeVisible({ timeout: 30_000 });

  // The pipeline's other end: outbox → broker → API consumer → hub push → badge.
  const bell = page.getByRole('button', { name: /notifications/i });
  await expect(bell).toContainText('1', { timeout: 30_000 });

  // The panel's mark-read call fires on mount and is fire-and-forget from the UI's
  // perspective (the badge clears optimistically, before the POST resolves). Waiting for
  // the response here — rather than just the optimistic badge state — is what makes the
  // read state's persistence below trustworthy: without it, reload can race the request
  // and cancel it mid-flight server-side, so the "read" never actually reaches the server.
  const markRead = page.waitForResponse(
    (res) => /\/notifications\/[^/]+\/read$/.test(res.url()) && res.request().method() === 'POST',
  );
  await bell.click();
  await expect(page.getByRole('heading', { name: 'Notifications' })).toBeVisible();
  await expect(
    page.getByText(new RegExp(`IVV crossed above \\$${threshold.replace('.', '\\.')}`)),
  ).toBeVisible();
  await markRead;

  // Opening was the acknowledgement: the badge clears, and stays cleared across a full
  // reload because read state lives on the server, not in the client.
  await expect(bell).not.toContainText('1');
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Watchlist' })).toBeVisible();
  await expect(page.getByRole('button', { name: /notifications/i })).not.toContainText('1');
});
