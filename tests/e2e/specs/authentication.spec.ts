import { expect, test } from '@playwright/test';

const PASSWORD = 'correct horse battery staple';

function uniqueEmail(): string {
  return `e2e-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@marketpulse.local`;
}

test('a new user can register, add a ticker, watch it tick, and sign out', async ({ page }) => {
  const email = uniqueEmail();

  await page.goto('/register');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Create account' }).click();

  // Registration lands on the protected watchlist.
  await expect(page.getByRole('heading', { name: 'Watchlist' })).toBeVisible();

  // A brand new account starts empty.
  await expect(page.getByRole('listitem')).toHaveCount(0);

  await page.getByLabel('Add ticker').fill('IVV');
  await page.getByRole('button', { name: 'Add', exact: true }).click();
  await expect(page.getByText('IVV')).toBeVisible();

  // The price cell is fed by SignalR, which authenticated off the same cookie. If the
  // hub rejected the connection this stays at the em-dash placeholder forever.
  // PriceCell already exposes aria-label={`${ticker} price`} — no source change needed.
  await expect(page.getByLabel('IVV price')).toHaveText(/^\$\d/, { timeout: 15_000 });

  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page).toHaveURL(/\/login$/);

  // The protected route is genuinely closed now, not merely hidden.
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
});

test('the session survives a full page reload', async ({ page }) => {
  const email = uniqueEmail();

  await page.goto('/register');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByRole('heading', { name: 'Watchlist' })).toBeVisible();

  await page.reload();

  // No re-login: the cookie outlived the page, which is the entire point of not
  // holding the token in JavaScript memory.
  await expect(page.getByRole('heading', { name: 'Watchlist' })).toBeVisible();
});

test('signing in with the wrong password shows an error and stays on the login page', async ({
  page,
}) => {
  const email = uniqueEmail();

  await page.goto('/register');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByRole('heading', { name: 'Watchlist' })).toBeVisible();
  await page.getByRole('button', { name: 'Sign out' }).click();

  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill('definitely the wrong password');
  await page.getByRole('button', { name: 'Sign in' }).click();

  await expect(page.getByRole('alert')).toContainText(/incorrect/i);
  await expect(page).toHaveURL(/\/login$/);
});
