import { expect, test } from '@playwright/test';

const PASSWORD = 'correct horse battery staple';

function uniqueEmail(): string {
  return `e2e-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@marketpulse.local`;
}

test('a buy and a higher sell produce correct holdings, P&L, and history', async ({
  page,
}) => {
  const email = uniqueEmail();

  await page.goto('/register');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByRole('heading', { name: 'Watchlist' })).toBeVisible();

  await page.getByRole('link', { name: 'Portfolio' }).click();
  await expect(page.getByRole('heading', { name: 'Portfolio' })).toBeVisible();
  await expect(page.getByText('No holdings yet. Record your first trade below.')).toBeVisible();

  // Buy 10 IVV @ 60 — user-typed prices make every P&L assertion deterministic.
  await page.getByLabel('Ticker').fill('IVV');
  await page.getByLabel('Side').selectOption('Buy');
  await page.getByLabel('Units').fill('10');
  await page.getByLabel('Price', { exact: true }).fill('60');
  await page.getByRole('button', { name: 'Record trade' }).click();

  const row = page.getByRole('row', { name: /IVV/ });
  // Cell order per HoldingsTable's <thead>: Ticker, Units, Avg cost, Last, Realised P&L,
  // Unrealised P&L. A row-wide toContainText('6') after the sell would be silently
  // satisfied by the avg-cost cell's "$60.00" even if units never decremented — so units
  // get their own cell, scoped and exact, not a row-wide substring.
  const unitsCell = row.getByRole('cell').nth(1);
  const avgCostCell = row.getByRole('cell').nth(2);
  await expect(unitsCell).toHaveText('10');
  await expect(avgCostCell).toHaveText('$60.00'); // average cost

  // Sell 4 @ 70 → realised 4 × (70 − 60) = +$40; 6 units remain at avg 60.
  await page.getByLabel('Ticker').fill('IVV');
  await page.getByLabel('Side').selectOption('Sell');
  await page.getByLabel('Units').fill('4');
  await page.getByLabel('Price', { exact: true }).fill('70');
  await page.getByRole('button', { name: 'Record trade' }).click();

  // Row-wide toContainText('6') would also be satisfied by the '$60.00' avg-cost cell
  // regardless of whether units ever decremented — cell-scoped so this is a real check.
  await expect(unitsCell).toHaveText('6');
  // Average cost is unchanged by a sell (only realised P&L moves) — the partial-sell
  // invariant, checked in the state the scenario is actually about.
  await expect(avgCostCell).toHaveText('$60.00');
  await expect(row).toContainText('+$40.00');

  // The live price cell fills from the stream — same tolerant first-tick assertion as
  // the watchlist journey; the P&L numbers above never depended on it.
  await expect(page.getByLabel('IVV price')).toHaveText(/^\$\d/, { timeout: 15_000 });

  // History, newest first.
  const items = page.getByRole('listitem');
  await expect(items.first()).toContainText('Sell 4 IVV @ $70.00');
  await expect(items.nth(1)).toContainText('Buy 10 IVV @ $60.00');

  // Everything survives a reload — server truth, not client state.
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Portfolio' })).toBeVisible();
  await expect(page.getByRole('row', { name: /IVV/ })).toContainText('+$40.00');
  await expect(page.getByRole('listitem').first()).toContainText('Sell 4 IVV @ $70.00');
});
