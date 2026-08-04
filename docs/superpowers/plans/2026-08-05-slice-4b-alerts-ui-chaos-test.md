# Slice 4b — Alerts UI and Chaos Test Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the 4a alerts pipeline visible (alerts UI + notifications panel with unread badge) and provable (Playwright journey + a chaos test that kills RabbitMQ mid-flow and shows zero lost alerts), plus the two carried-over messaging fixes and ADR-007.

**Architecture:** The dashboard gains two feature folders shaped like the existing three — `features/alerts` (TanStack Query over the 4a alert CRUD endpoints, inline rule control on watchlist rows) and `features/notifications` (a SignalR hook mirroring `usePriceStream` that patches pushes into the TanStack Query cache; unread badge derived from that cache, never stored). No new backend endpoints. The chaos test composes the real API host and real worker services against Testcontainers, driving outbox dispatch by hand so the broker can be stopped deterministically inside the Triggered→dispatched window.

**Tech Stack:** React 18 + TanStack Query + @microsoft/signalr + zod (`packages/api-client`), CSS modules on `--mp-*` tokens (`packages/ui`), Vitest + msw + testing-library, .NET 10 + xUnit + NSubstitute + Testcontainers, Playwright.

## Global Constraints

- Branch: `feature/slice-4b-alerts-ui-chaos-test` (already exists; spec committed on it). Merges into `test`, never directly into `main`.
- Commit messages: conventional-commit style, **no Co-Authored-By trailer** (repo convention).
- Spec: `docs/superpowers/specs/2026-08-05-alerts-ui-chaos-test-design.md`. Out of scope there is out of scope here: no Zustand, no `/alerts` route, no mark-all-read endpoint, no toasts, no new rule types.
- All client-server traffic goes through `packages/api-client` — no `fetch` in `apps/dashboard`.
- `DependencyRuleTests` must stay green: nothing new in Domain/Application, no Application→RabbitMQ reference.
- Frontend a11y convention: colour is never the only signal; interactive controls get accessible names; per-row controls parameterise the name with the ticker (`Set alert for IVV`).
- Run frontend tests with `pnpm --filter <pkg> test -- --run` (Vitest watch mode is the default otherwise). Run .NET tests with `dotnet test --filter <name>` from the repo root. Integration tests need Docker running.
- JSON casing on the wire is camelCase; enum-ish fields (`direction`, `status`) travel as strings (`"Above"`/`"Below"`, `"Active"`/`"Triggered"`).

---

### Task 1: Fix the publisher dispose race (4a carryover)

`RabbitMqEventPublisher.DisposeAsync` sets `_disposed` but does not take `_gate` before disposing it, so a `PublishAsync` already past the flag check can meet a disposed semaphore. Copy `RabbitMqConnection`'s shape: a shutdown CTS to interrupt in-flight channel acquisition, then take the gate, then dispose.

**Files:**
- Modify: `src/MarketPulse.Infrastructure/Messaging/RabbitMqEventPublisher.cs`
- Test: `tests/MarketPulse.UnitTests/Messaging/RabbitMqEventPublisherTests.cs`

**Interfaces:**
- Consumes: existing internal test seam `RabbitMqEventPublisher(Func<bool, CancellationToken, Task<IChannel>>, IOptions<RabbitMqOptions>, ILogger<RabbitMqEventPublisher>)`.
- Produces: no signature changes; behavioural contract only (disposal serialises against in-flight publishes).

- [ ] **Step 1: Write the failing test**

Append to `RabbitMqEventPublisherTests` (the `Options`, `Channel`, `Publish` helpers already exist in the file):

```csharp
[Fact]
public async Task Disposal_interrupts_an_in_flight_channel_acquisition_instead_of_racing_it()
{
    // The race the _disposed flag alone cannot close: a publish that has passed the
    // flag check and is holding the gate — parked in channel creation against a broker
    // that is not answering — meets DisposeAsync tearing the semaphore down under it.
    var channelRequested = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);

    var publisher = new RabbitMqEventPublisher(
        async (_, ct) =>
        {
            channelRequested.TrySetResult();

            // Only cancellation ends this, exactly like a broker that never answers.
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Channel(open: true); // unreachable
        },
        Options,
        NullLogger<RabbitMqEventPublisher>.Instance);

    var publish = Publish(publisher);
    await channelRequested.Task;

    // Disposal must complete promptly — the in-flight acquisition is interrupted, not
    // waited out and not raced.
    await publisher.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    // And the interrupted publish surfaces cancellation — never an
    // ObjectDisposedException from a SemaphoreSlim the caller has never heard of.
    await Assert.ThrowsAnyAsync<OperationCanceledException>(
        () => publish.WaitAsync(TimeSpan.FromSeconds(5)));
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/MarketPulse.UnitTests --filter Disposal_interrupts_an_in_flight_channel_acquisition_instead_of_racing_it`
Expected: FAIL — the publish task never completes (nothing cancels the parked delegate), so the final `WaitAsync` throws `TimeoutException`.

- [ ] **Step 3: Implement the fix**

In `RabbitMqEventPublisher.cs`:

Add the field beside `_gate`:

```csharp
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
```

In `ChannelAsync`, link the caller's token to shutdown (mirroring `RabbitMqConnection.GetAsync`) — replace the `await _gate.WaitAsync(ct);` line and thread the linked token through:

```csharp
        // Linked so disposal mid-acquisition cancels this attempt instead of leaving
        // DisposeAsync waiting on the gate for as long as the broker stays silent.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, _shutdownCts.Token);

        await _gate.WaitAsync(linked.Token);
```

and inside the `try`, pass `linked.Token` to channel creation:

```csharp
            var channel = await _createChannel(true, linked.Token);
            await RabbitMqTopology.DeclareAsync(channel, _options, linked.Token);
```

Replace `DisposeAsync` entirely — this is `RabbitMqConnection.DisposeAsync`'s shape:

```csharp
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        // Set before anything else so a publish that has not yet reached the gate fails
        // as "this publisher is gone" rather than anything stranger.
        _disposed = true;

        // Interrupt any in-flight channel acquisition — including one parked against a
        // silent broker — so acquiring the gate below cannot block indefinitely.
        await _shutdownCts.CancelAsync();

        await _gate.WaitAsync();

        try
        {
            if (_channel is not null)
            {
                try
                {
                    await _channel.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex, "Failed to dispose the publisher channel during shutdown.");
                }

                _channel = null;
            }
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
        _shutdownCts.Dispose();
    }
```

Also correct the over-promising comment on the flag check at the top of `PublishAsync` — it currently claims the flag alone prevents the semaphore exception. Replace:

```csharp
        // Checked here so a publish that races shutdown fails as this object being gone,
        // rather than as an ObjectDisposedException from a semaphore the caller has never
        // heard of. RabbitMqTickSink already guards itself this way.
```

with:

```csharp
        // First line of defence: a publish arriving after disposal fails as this object
        // being gone. A publish already *inside* ChannelAsync when disposal starts is
        // handled by DisposeAsync cancelling _shutdownCts and taking _gate before it
        // disposes anything — the flag alone cannot close that window.
```

- [ ] **Step 4: Run the messaging unit tests**

Run: `dotnet test tests/MarketPulse.UnitTests --filter RabbitMqEventPublisher`
Expected: PASS, including the four pre-existing tests (`Publishing_after_disposal…`, `Disposing_twice_is_harmless`, both channel-replacement tests).

- [ ] **Step 5: Commit**

```bash
git add src/MarketPulse.Infrastructure/Messaging/RabbitMqEventPublisher.cs tests/MarketPulse.UnitTests/Messaging/RabbitMqEventPublisherTests.cs
git commit -m "fix(messaging): serialise publisher disposal against in-flight publishes"
```

---

### Task 2: Floor the consumer's resubscribe delay (4a carryover)

`RabbitMqConsumerService` resets its retry delay to zero after every successful subscribe, so a channel that dies immediately after each subscribe re-loops with no backoff at all. A floor of one first-retry delay after a shutdown-triggered resubscribe removes the hot-loop class without slowing genuine recovery meaningfully.

**Files:**
- Modify: `src/MarketPulse.Infrastructure/Messaging/RabbitMqConsumerService.cs`
- Test: `tests/MarketPulse.UnitTests/Messaging/RabbitMqConsumerServiceTests.cs`

**Interfaces:**
- Consumes: existing `TestConsumer` subclass and `OpenChannel()`/`WaitUntilAsync()`/`SubscribedTo()` helpers in the test file; `TestConsumer` passes `firstRetryDelay: TimeSpan.FromMilliseconds(5)`.
- Produces: no signature changes.

- [ ] **Step 1: Write the failing test**

Append to `RabbitMqConsumerServiceTests`. It measures the gap between consecutive `createChannel` calls with `Stopwatch` timestamps (wall-clock `DateTime` is too coarse for a 5 ms floor):

```csharp
[Fact]
public async Task A_resubscribe_after_a_channel_shutdown_waits_at_least_one_delay()
{
    // Without the floor, a channel that dies immediately after every successful
    // subscribe re-loops with no backoff at all: subscribe, die, subscribe, die — a
    // hot loop the reset-on-success behaviour was never meant to allow.
    var handed = new List<(IChannel Channel, long Timestamp)>();

    Task<IChannel> Create(bool _, CancellationToken __)
    {
        var channel = OpenChannel();

        lock (handed)
        {
            handed.Add((channel, Stopwatch.GetTimestamp()));
        }

        return Task.FromResult(channel);
    }

    var consumer = new TestConsumer(Create);
    await consumer.StartAsync(CancellationToken.None);

    try
    {
        for (var round = 1; round <= 3; round++)
        {
            var expected = round;
            await WaitUntilAsync(
                () => { lock (handed) { return handed.Count >= expected; } },
                $"subscription {round}");

            IChannel current;
            lock (handed)
            {
                current = handed[expected - 1].Channel;
            }

            current.ChannelShutdownAsync += Raise.Event<AsyncEventHandler<ShutdownEventArgs>>(
                current,
                new ShutdownEventArgs(ShutdownInitiator.Library, 541, "connection lost"));
        }

        await WaitUntilAsync(
            () => { lock (handed) { return handed.Count >= 4; } },
            "the final resubscription");

        lock (handed)
        {
            for (var i = 1; i < 4; i++)
            {
                var gap = Stopwatch.GetElapsedTime(
                    handed[i - 1].Timestamp, handed[i].Timestamp);

                // TestConsumer's firstRetryDelay is 5 ms. Task.Delay only ever waits at
                // least its argument, so the floor is a safe lower bound to assert.
                Assert.True(
                    gap >= TimeSpan.FromMilliseconds(5),
                    $"Resubscribe {i} happened after only {gap.TotalMilliseconds:F2} ms.");
            }
        }
    }
    finally
    {
        await consumer.StopAsync(CancellationToken.None);
        consumer.Dispose();
    }
}
```

Add `using System.Diagnostics;` to the file's usings if not present.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/MarketPulse.UnitTests --filter A_resubscribe_after_a_channel_shutdown_waits_at_least_one_delay`
Expected: FAIL — at least one gap is well under 5 ms, because the loop re-enters with `retryDelay == TimeSpan.Zero`.

- [ ] **Step 3: Implement the fix**

In `RabbitMqConsumerService.ExecuteAsync`, the successful-subscribe path currently ends:

```csharp
                await shutdown.Task.WaitAsync(stoppingToken);
```

Change it to set the floor once the shutdown has actually happened:

```csharp
                await shutdown.Task.WaitAsync(stoppingToken);

                // A floor of one delay, not another zero: a channel that dies
                // immediately after every successful subscribe must not turn this loop
                // hot. One first-retry delay is invisible during a real outage and
                // removes the spin class entirely.
                retryDelay = _firstRetryDelay;
```

Leave the existing `retryDelay = TimeSpan.Zero;` reset after `BasicConsumeAsync` exactly where it is — a *successful* subscribe still forgives the accumulated backoff; only the subsequent death re-arms it. Amend that reset's comment to name the pairing:

```csharp
                // A subscription that got this far resets the backoff, so an outage that
                // lasted an hour does not leave the next one starting at the cap. The
                // floor below (after the shutdown await) is what stops this reset from
                // enabling a hot loop when channels die immediately after subscribing.
                retryDelay = TimeSpan.Zero;
```

- [ ] **Step 4: Run the consumer unit tests**

Run: `dotnet test tests/MarketPulse.UnitTests --filter RabbitMqConsumerService`
Expected: PASS, including the pre-existing shutdown-replacement and failure-retry tests.

- [ ] **Step 5: Commit**

```bash
git add src/MarketPulse.Infrastructure/Messaging/RabbitMqConsumerService.cs tests/MarketPulse.UnitTests/Messaging/RabbitMqConsumerServiceTests.cs
git commit -m "fix(messaging): floor the consumer resubscribe delay after a channel shutdown"
```

---

### Task 3: api-client schemas and methods for alerts and notifications

**Files:**
- Modify: `packages/api-client/src/schemas.ts`
- Modify: `packages/api-client/src/client.ts`
- Modify: `packages/api-client/src/index.ts` (re-exports; mirror how existing schemas/types are exported)
- Test: `packages/api-client/src/schemas.test.ts`, `packages/api-client/src/client.test.ts`

**Interfaces:**
- Consumes: `request<T>()` helper and `createApiClient` structure in `client.ts`.
- Produces (later tasks rely on these exact names):
  - Schemas/types: `alertRuleSchema`, `AlertRule`, `notificationSchema`, `Notification`, `notificationPushSchema`, `NotificationPush`, `AlertDirection` (`'Above' | 'Below'`).
  - Client methods: `getAlerts(signal?): Promise<AlertRule[]>`, `createAlert(ticker: string, direction: AlertDirection, threshold: number, signal?): Promise<AlertRule>`, `deleteAlert(id: string, signal?): Promise<void>`, `rearmAlert(id: string, signal?): Promise<AlertRule>`, `getNotifications(signal?): Promise<Notification[]>`, `markNotificationRead(id: string, signal?): Promise<void>`.

- [ ] **Step 1: Write the failing schema tests**

Append to `schemas.test.ts` (follow the file's existing describe/it conventions):

```ts
describe('alertRuleSchema', () => {
  it('parses a rule as the API serialises it', () => {
    const rule = alertRuleSchema.parse({
      id: 'a1',
      ticker: 'IVV',
      direction: 'Above',
      threshold: 65.5,
      status: 'Active',
      createdUtc: '2026-08-05T00:00:00+00:00',
      triggeredUtc: null,
      triggeredPrice: null,
    });

    expect(rule.direction).toBe('Above');
  });

  it('rejects a direction outside Above/Below', () => {
    expect(() =>
      alertRuleSchema.parse({
        id: 'a1',
        ticker: 'IVV',
        direction: 'Sideways',
        threshold: 65.5,
        status: 'Active',
        createdUtc: '2026-08-05T00:00:00+00:00',
        triggeredUtc: null,
        triggeredPrice: null,
      }),
    ).toThrow();
  });
});

describe('notification schemas', () => {
  it('parses an API row including its read flag', () => {
    const n = notificationSchema.parse({
      id: 'n1',
      alertRuleId: 'a1',
      ticker: 'IVV',
      direction: 'Above',
      threshold: 60,
      triggeredPrice: 61.2,
      occurredUtc: '2026-08-05T00:00:00+00:00',
      isRead: false,
    });

    expect(n.isRead).toBe(false);
  });

  it('parses a hub push, which carries no read flag', () => {
    const p = notificationPushSchema.parse({
      id: 'n1',
      alertRuleId: 'a1',
      ticker: 'IVV',
      direction: 'Above',
      threshold: 60,
      triggeredPrice: 61.2,
      occurredUtc: '2026-08-05T00:00:00+00:00',
    });

    expect(p.ticker).toBe('IVV');
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `pnpm --filter @marketpulse/api-client test -- --run`
Expected: FAIL — the schemas don't exist yet.

- [ ] **Step 3: Add the schemas**

Append to `schemas.ts`:

```ts
export const alertDirectionSchema = z.enum(['Above', 'Below']);

export const alertRuleSchema = z.object({
  id: z.string(),
  ticker: z.string().min(1).max(8),
  direction: alertDirectionSchema,
  threshold: z.number().positive(),
  status: z.enum(['Active', 'Triggered']),
  createdUtc: z.string(),
  triggeredUtc: z.string().nullable(),
  triggeredPrice: z.number().nullable(),
});

export const notificationSchema = z.object({
  id: z.string(),
  alertRuleId: z.string(),
  ticker: z.string().min(1).max(8),
  direction: alertDirectionSchema,
  threshold: z.number(),
  triggeredPrice: z.number(),
  occurredUtc: z.string(),
  isRead: z.boolean(),
});

/** The hub payload is the notification minus its read flag — a push is unread by definition. */
export const notificationPushSchema = notificationSchema.omit({ isRead: true });

export type AlertDirection = z.infer<typeof alertDirectionSchema>;
export type AlertRule = z.infer<typeof alertRuleSchema>;
export type Notification = z.infer<typeof notificationSchema>;
export type NotificationPush = z.infer<typeof notificationPushSchema>;
```

- [ ] **Step 4: Write the failing client tests**

Append to `client.test.ts` (uses the file's existing `jsonResponse` helper and `fetchMock` setup):

```ts
it('parses the alert list and hits the alerts endpoint', async () => {
  fetchMock.mockResolvedValue(
    jsonResponse([
      {
        id: 'a1',
        ticker: 'IVV',
        direction: 'Above',
        threshold: 60,
        status: 'Active',
        createdUtc: '2026-08-05T00:00:00+00:00',
        triggeredUtc: null,
        triggeredPrice: null,
      },
    ]),
  );

  const rules = await createApiClient(BASE).getAlerts();

  expect(fetchMock.mock.calls[0]?.[0]).toBe(`${BASE}/api/v1/alerts`);
  expect(rules[0]?.status).toBe('Active');
});

it('re-arms by id with the CSRF header attached', async () => {
  fetchMock.mockResolvedValue(
    jsonResponse({
      id: 'a1',
      ticker: 'IVV',
      direction: 'Above',
      threshold: 60,
      status: 'Active',
      createdUtc: '2026-08-05T00:00:00+00:00',
      triggeredUtc: null,
      triggeredPrice: null,
    }),
  );

  await createApiClient(BASE).rearmAlert('a1');

  expect(fetchMock.mock.calls[0]?.[0]).toBe(`${BASE}/api/v1/alerts/a1/rearm`);
  const init = fetchMock.mock.calls[0]?.[1] as RequestInit;
  expect((init.headers as Record<string, string>)['X-CSRF-Token']).toBe('nonce-123');
});

it('marks a notification read against its own id', async () => {
  fetchMock.mockResolvedValue(new Response(null, { status: 204 }));

  await createApiClient(BASE).markNotificationRead('n1');

  expect(fetchMock.mock.calls[0]?.[0]).toBe(`${BASE}/api/v1/notifications/n1/read`);
  expect((fetchMock.mock.calls[0]?.[1] as RequestInit).method).toBe('POST');
});
```

- [ ] **Step 5: Add the client methods**

In `client.ts`, extend the schema import and append to the returned object after `removeItem`:

```ts
    getAlerts: (signal?: AbortSignal): Promise<AlertRule[]> =>
      request('/api/v1/alerts', { method: 'GET', signal }, (d) =>
        z.array(alertRuleSchema).parse(d),
      ),

    createAlert: (
      ticker: string,
      direction: AlertDirection,
      threshold: number,
      signal?: AbortSignal,
    ): Promise<AlertRule> =>
      request(
        '/api/v1/alerts',
        { method: 'POST', body: JSON.stringify({ ticker, direction, threshold }), signal },
        (d) => alertRuleSchema.parse(d),
      ),

    deleteAlert: (id: string, signal?: AbortSignal): Promise<void> =>
      request(
        `/api/v1/alerts/${encodeURIComponent(id)}`,
        { method: 'DELETE', signal },
        () => undefined,
      ),

    rearmAlert: (id: string, signal?: AbortSignal): Promise<AlertRule> =>
      request(
        `/api/v1/alerts/${encodeURIComponent(id)}/rearm`,
        { method: 'POST', signal },
        (d) => alertRuleSchema.parse(d),
      ),

    getNotifications: (signal?: AbortSignal): Promise<Notification[]> =>
      request('/api/v1/notifications', { method: 'GET', signal }, (d) =>
        z.array(notificationSchema).parse(d),
      ),

    markNotificationRead: (id: string, signal?: AbortSignal): Promise<void> =>
      request(
        `/api/v1/notifications/${encodeURIComponent(id)}/read`,
        { method: 'POST', signal },
        () => undefined,
      ),
```

Import `z` plus `alertRuleSchema`, `notificationSchema`, and the `AlertRule`, `AlertDirection`, `Notification` types at the top, alongside the existing schema imports. Update `index.ts` so every new schema and type is re-exported the same way the existing ones are.

- [ ] **Step 6: Run the package tests and build**

Run: `pnpm --filter @marketpulse/api-client test -- --run && pnpm --filter @marketpulse/api-client build`
Expected: PASS / clean build.

- [ ] **Step 7: Commit**

```bash
git add packages/api-client/src
git commit -m "feat(api-client): alerts and notifications schemas and methods"
```

---

### Task 4: Badge primitive in packages/ui

**Files:**
- Create: `packages/ui/src/components/Badge/Badge.tsx`
- Create: `packages/ui/src/components/Badge/Badge.module.css`
- Create: `packages/ui/src/components/Badge/Badge.test.tsx`
- Modify: `packages/ui/src/index.ts`

**Interfaces:**
- Produces: `Badge({ count, label, max = 9 }: BadgeProps)` — renders nothing when `count <= 0`; visible text caps at `max` as `"9+"`; screen-reader text is `` `${count} ${label}` `` via `VisuallyHidden`.

- [ ] **Step 1: Write the failing test**

```tsx
import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Badge } from './Badge';

describe('Badge', () => {
  it('renders the count with an accessible description', () => {
    render(<Badge count={3} label="unread notifications" />);

    expect(screen.getByText('3')).toBeInTheDocument();
    expect(screen.getByText('3 unread notifications')).toBeInTheDocument();
  });

  it('caps the visible count but keeps the real one for screen readers', () => {
    render(<Badge count={12} label="unread notifications" />);

    expect(screen.getByText('9+')).toBeInTheDocument();
    expect(screen.getByText('12 unread notifications')).toBeInTheDocument();
  });

  it('renders nothing at zero', () => {
    const { container } = render(<Badge count={0} label="unread notifications" />);

    expect(container).toBeEmptyDOMElement();
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `pnpm --filter @marketpulse/ui test -- --run`
Expected: FAIL — module does not exist.

- [ ] **Step 3: Implement**

`Badge.tsx`:

```tsx
import { VisuallyHidden } from '../VisuallyHidden/VisuallyHidden';
import styles from './Badge.module.css';

export interface BadgeProps {
  count: number;
  /** What the count means, e.g. "unread notifications". Read by screen readers. */
  label: string;
  /** Visible display caps here as "9+"; the accessible text keeps the real count. */
  max?: number;
}

/** Renders nothing at zero: an empty badge is noise, and its accessible name goes with it. */
export function Badge({ count, label, max = 9 }: BadgeProps) {
  if (count <= 0) return null;

  const display = count > max ? `${max}+` : String(count);

  return (
    <span className={styles.badge}>
      <span aria-hidden="true">{display}</span>
      <VisuallyHidden>{`${count} ${label}`}</VisuallyHidden>
    </span>
  );
}
```

`Badge.module.css` (same token vocabulary as Button's primary variant):

```css
.badge {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 1.25rem;
  height: 1.25rem;
  padding: 0 var(--mp-space-1);
  border-radius: 999px;
  background: var(--mp-accent);
  color: var(--mp-surface-base);
  font-size: 0.75rem;
  font-weight: 600;
  line-height: 1;
}
```

Add to `index.ts`, alphabetically with the others:

```ts
export { Badge, type BadgeProps } from './components/Badge/Badge';
```

- [ ] **Step 4: Run the UI package tests**

Run: `pnpm --filter @marketpulse/ui test -- --run && pnpm --filter @marketpulse/ui build`
Expected: PASS (including the existing token-leak tests — the css above uses only `--mp-*` semantic tokens).

- [ ] **Step 5: Commit**

```bash
git add packages/ui/src
git commit -m "feat(ui): Badge primitive for unread counts"
```

---

### Task 5: Alerts feature — hooks and inline watchlist rule control

**Files:**
- Create: `apps/dashboard/src/features/alerts/useAlerts.ts`
- Create: `apps/dashboard/src/features/alerts/AlertCell.tsx`
- Create: `apps/dashboard/src/features/alerts/AlertCell.module.css`
- Create: `apps/dashboard/src/features/alerts/AlertCell.test.tsx`
- Modify: `apps/dashboard/src/features/watchlist/WatchlistScreen.tsx` (new Alert column)
- Modify: `apps/dashboard/src/features/watchlist/WatchlistScreen.test.tsx` (msw handler for `GET /api/v1/alerts` — the screen now fetches it, and msw is configured with `onUnhandledRequest: 'error'`)

**Interfaces:**
- Consumes: `apiClient` from `../../api`; `AlertRule`, `AlertDirection`, `ApiError` from `@marketpulse/api-client`; `Alert`, `Button`, `TextField` from `@marketpulse/ui`.
- Produces (Task 6 and the e2e journey rely on these):
  - `alertsKey = ['alerts'] as const` exported from `useAlerts.ts`.
  - Hooks: `useAlerts()`, `useCreateAlert()`, `useDeleteAlert()`, `useRearmAlert()` — mutations invalidate `alertsKey`.
  - `AlertCell({ ticker }: { ticker: string })` with accessible names parameterised by ticker: `Alert direction for {ticker}`, `Alert threshold for {ticker}`, button `Set alert for {ticker}`, button `Remove alert for {ticker}`, button `Re-arm alert for {ticker}`; rule state text is exactly `Active` or `Triggered`.

- [ ] **Step 1: Write `useAlerts.ts`** (hook shapes copied from `useWatchlist.ts`; invalidate rather than setQueryData because create/delete/rearm return one rule, not the list)

```ts
import { ApiError, type AlertDirection, type AlertRule } from '@marketpulse/api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../../api';

export const alertsKey = ['alerts'] as const;

export function useAlerts() {
  return useQuery({
    queryKey: alertsKey,
    queryFn: ({ signal }) => apiClient.getAlerts(signal),
  });
}

export function useCreateAlert() {
  const queryClient = useQueryClient();

  return useMutation<
    AlertRule,
    ApiError,
    { ticker: string; direction: AlertDirection; threshold: number }
  >({
    mutationFn: ({ ticker, direction, threshold }) =>
      apiClient.createAlert(ticker, direction, threshold),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: alertsKey }),
  });
}

export function useDeleteAlert() {
  const queryClient = useQueryClient();

  return useMutation<void, ApiError, string>({
    mutationFn: (id) => apiClient.deleteAlert(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: alertsKey }),
  });
}

export function useRearmAlert() {
  const queryClient = useQueryClient();

  return useMutation<AlertRule, ApiError, string>({
    mutationFn: (id) => apiClient.rearmAlert(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: alertsKey }),
  });
}
```

- [ ] **Step 2: Write the failing component tests**

`AlertCell.test.tsx` (msw setup copied from `WatchlistScreen.test.tsx`):

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { AlertCell } from './AlertCell';

const activeRule = {
  id: 'a1',
  ticker: 'IVV',
  direction: 'Above',
  threshold: 60,
  status: 'Active',
  createdUtc: '2026-08-05T00:00:00+00:00',
  triggeredUtc: null,
  triggeredPrice: null,
};

const server = setupServer(
  http.get('http://localhost:5100/api/v1/alerts', () => HttpResponse.json([])),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

function renderCell(ticker = 'IVV') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <AlertCell ticker={ticker} />
    </QueryClientProvider>,
  );
}

describe('AlertCell', () => {
  it('offers the create form when the ticker has no rule', async () => {
    renderCell();

    expect(await screen.findByLabelText('Alert threshold for IVV')).toBeInTheDocument();
    expect(screen.getByLabelText('Alert direction for IVV')).toBeInTheDocument();
  });

  it('creates a rule and swaps to its status', async () => {
    // Stateful handlers: after the POST succeeds, the invalidation-triggered refetch
    // must return the new rule for the cell to swap from form to status.
    let posted: unknown;
    let created = false;
    server.use(
      http.get('http://localhost:5100/api/v1/alerts', () =>
        HttpResponse.json(created ? [activeRule] : []),
      ),
      http.post('http://localhost:5100/api/v1/alerts', async ({ request }) => {
        posted = await request.json();
        created = true;
        return HttpResponse.json(activeRule, { status: 201 });
      }),
    );

    renderCell();

    await userEvent.selectOptions(
      await screen.findByLabelText('Alert direction for IVV'),
      'Above',
    );
    await userEvent.type(screen.getByLabelText('Alert threshold for IVV'), '60');
    await userEvent.click(screen.getByRole('button', { name: 'Set alert for IVV' }));

    expect(await screen.findByText('Active')).toBeInTheDocument();
    expect(posted).toEqual({ ticker: 'IVV', direction: 'Above', threshold: 60 });
  });

  it('shows a triggered rule with a re-arm action', async () => {
    server.use(
      http.get('http://localhost:5100/api/v1/alerts', () =>
        HttpResponse.json([
          { ...activeRule, status: 'Triggered', triggeredPrice: 61.2 },
        ]),
      ),
    );

    renderCell();

    expect(await screen.findByText('Triggered')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Re-arm alert for IVV' })).toBeInTheDocument();
  });

  it('surfaces the server message when creation is rejected', async () => {
    server.use(
      http.post('http://localhost:5100/api/v1/alerts', () =>
        HttpResponse.json(
          { title: 'unknown-ticker', status: 400, detail: "'ZZZ' is not a known ticker." },
          { status: 400 },
        ),
      ),
    );

    renderCell('ZZZ');

    await userEvent.type(await screen.findByLabelText('Alert threshold for ZZZ'), '10');
    await userEvent.click(screen.getByRole('button', { name: 'Set alert for ZZZ' }));

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent("'ZZZ' is not a known ticker."),
    );
  });
});
```

- [ ] **Step 3: Run to verify failure**

Run: `pnpm --filter @marketpulse/dashboard test -- --run src/features/alerts`
Expected: FAIL — `AlertCell` does not exist.

- [ ] **Step 4: Implement `AlertCell.tsx`**

One rule per ticker in the UI (the first from the list); the aggregate allows more, but a second rule on the same ticker from this control is deliberately not offered — YAGNI, recorded in the spec's scope.

```tsx
import { Alert, Button } from '@marketpulse/ui';
import { useState, type FormEvent } from 'react';
import styles from './AlertCell.module.css';
import { useAlerts, useCreateAlert, useDeleteAlert, useRearmAlert } from './useAlerts';

export interface AlertCellProps {
  ticker: string;
}

/**
 * The rule inline with the price it watches. No rule: a compact create form.
 * A rule: its state (Active/Triggered), re-arm when triggered, and remove.
 */
export function AlertCell({ ticker }: AlertCellProps) {
  const { data: rules } = useAlerts();
  const createAlert = useCreateAlert();
  const deleteAlert = useDeleteAlert();
  const rearmAlert = useRearmAlert();
  const [threshold, setThreshold] = useState('');
  const [direction, setDirection] = useState<'Above' | 'Below'>('Above');

  const rule = rules?.find((r) => r.ticker === ticker);

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    const value = Number(threshold);
    if (!Number.isFinite(value) || value <= 0) return;

    createAlert.mutate(
      { ticker, direction, threshold: value },
      { onSuccess: () => setThreshold('') },
    );
  }

  if (rule) {
    return (
      <div className={styles.cell}>
        <span className={styles.status} data-status={rule.status}>
          {rule.status}
        </span>
        <span className={styles.rule}>
          {rule.direction === 'Above' ? '≥' : '≤'} ${rule.threshold.toFixed(2)}
        </span>
        {rule.status === 'Triggered' && (
          <Button
            variant="ghost"
            onClick={() => rearmAlert.mutate(rule.id)}
            aria-label={`Re-arm alert for ${ticker}`}
            disabled={rearmAlert.isPending}
          >
            Re-arm
          </Button>
        )}
        <Button
          variant="ghost"
          onClick={() => deleteAlert.mutate(rule.id)}
          aria-label={`Remove alert for ${ticker}`}
          disabled={deleteAlert.isPending}
        >
          ✕
        </Button>
      </div>
    );
  }

  return (
    <form className={styles.cell} onSubmit={handleSubmit}>
      <select
        className={styles.direction}
        value={direction}
        onChange={(e) => setDirection(e.target.value as 'Above' | 'Below')}
        aria-label={`Alert direction for ${ticker}`}
      >
        <option value="Above">Above</option>
        <option value="Below">Below</option>
      </select>
      <input
        className={styles.threshold}
        type="number"
        step="0.01"
        min="0.01"
        value={threshold}
        onChange={(e) => setThreshold(e.target.value)}
        aria-label={`Alert threshold for ${ticker}`}
      />
      <Button type="submit" disabled={createAlert.isPending} aria-label={`Set alert for ${ticker}`}>
        Set
      </Button>
      {createAlert.isError && <Alert>{createAlert.error.message}</Alert>}
    </form>
  );
}
```

Note: use a plain native `select`/number `input` styled from tokens rather than growing `TextField` — a table cell needs a compact form, and `TextField`'s stacked label layout is wrong here. The `aria-label` is the accessible name on both controls; it is what the tests (and the e2e journey) query.

`AlertCell.module.css`:

```css
.cell {
  display: flex;
  align-items: center;
  gap: var(--mp-space-2);
}

.status[data-status='Triggered'] {
  color: var(--mp-price-up);
  font-weight: 600;
}

.status[data-status='Active'] {
  color: var(--mp-text-secondary);
}

.rule {
  color: var(--mp-text-muted);
  font-family: var(--mp-font-mono);
  font-size: 0.8125rem;
}

.direction,
.threshold {
  padding: var(--mp-space-1) var(--mp-space-2);
  border: 1px solid var(--mp-border-subtle);
  border-radius: var(--mp-radius-sm);
  background: var(--mp-surface-base);
  color: var(--mp-text-primary);
  font: inherit;
}

.threshold {
  width: 5.5rem;
}
```

(If the app's global tokens are not available in `apps/dashboard` css modules by another name, match whatever `WatchlistScreen.module.css` does — same var names, no raw colours.)

- [ ] **Step 5: Wire the column into `WatchlistScreen.tsx`**

Add the import, a header cell after "Last", and the cell in the row body:

```tsx
import { AlertCell } from '../alerts/AlertCell';
```

```tsx
                <th scope="col" className={styles.numeric}>
                  Last
                </th>
                <th scope="col">Alert</th>
```

```tsx
                    <td>
                      <AlertCell ticker={item.ticker} />
                    </td>
```

(placed between the price cell and the actions cell).

- [ ] **Step 6: Keep the existing screen tests green**

`WatchlistScreen.test.tsx`'s msw server now needs the alerts handler — add to the `setupServer(...)` call:

```ts
  http.get('http://localhost:5100/api/v1/alerts', () => HttpResponse.json([])),
```

- [ ] **Step 7: Run the dashboard tests**

Run: `pnpm --filter @marketpulse/dashboard test -- --run`
Expected: PASS — new AlertCell tests and all pre-existing tests.

- [ ] **Step 8: Commit**

```bash
git add apps/dashboard/src/features/alerts apps/dashboard/src/features/watchlist
git commit -m "feat(dashboard): inline alert rules on watchlist rows"
```

---

### Task 6: Notifications feature — stream, bell, panel, unread badge

**Files:**
- Create: `apps/dashboard/src/features/realtime/reconnectPolicy.ts` (extracted from `usePriceStream.ts`)
- Modify: `apps/dashboard/src/features/prices/usePriceStream.ts` (import the extracted policy; behaviour unchanged)
- Create: `apps/dashboard/src/features/notifications/useNotifications.ts`
- Create: `apps/dashboard/src/features/notifications/useNotificationStream.ts`
- Create: `apps/dashboard/src/features/notifications/NotificationBell.tsx`
- Create: `apps/dashboard/src/features/notifications/NotificationsPanel.tsx`
- Create: `apps/dashboard/src/features/notifications/Notifications.module.css`
- Create: `apps/dashboard/src/features/notifications/useNotificationStream.test.ts`
- Create: `apps/dashboard/src/features/notifications/NotificationBell.test.tsx`
- Modify: `apps/dashboard/src/App.tsx` (bell in the header)

**Interfaces:**
- Consumes: `alertsKey` from `../alerts/useAlerts`; `useSession` from `../auth/useSession`; `notificationSchema`/`notificationPushSchema`/`Notification` from `@marketpulse/api-client`; `Badge`, `Button`, `Panel` from `@marketpulse/ui`; `API_BASE_URL` from `../../api`.
- Produces:
  - `notificationsKey = ['notifications'] as const` from `useNotifications.ts`; hooks `useNotifications(enabled: boolean)`, `useMarkRead()` (optimistic `isRead: true` patch, invalidate on error).
  - `useNotificationStream(enabled: boolean)` — no return value; patches the query cache.
  - `NotificationBell()` — renders nothing without a session; button accessible name starts with "Notifications"; panel heading is exactly `Notifications`; notification row copy is exactly `` `${ticker} crossed ${direction.toLowerCase()} $${threshold.toFixed(2)} — $${triggeredPrice.toFixed(2)}` `` (the e2e journey asserts on this).

- [ ] **Step 1: Extract the reconnect policy**

`features/realtime/reconnectPolicy.ts` — move `RECONNECT_DELAYS_MS`, `FINAL_RECONNECT_DELAY_MS`, and `infiniteReconnectPolicy` verbatim out of `usePriceStream.ts` (keeping the explanatory comment), exporting `infiniteReconnectPolicy`. Update `usePriceStream.ts` to `import { infiniteReconnectPolicy } from '../realtime/reconnectPolicy';` and delete the moved lines.

Run: `pnpm --filter @marketpulse/dashboard test -- --run src/features/prices`
Expected: PASS unchanged.

- [ ] **Step 2: Write `useNotifications.ts`**

```ts
import { ApiError, type Notification } from '@marketpulse/api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../../api';

export const notificationsKey = ['notifications'] as const;

export function useNotifications(enabled: boolean) {
  return useQuery({
    queryKey: notificationsKey,
    queryFn: ({ signal }) => apiClient.getNotifications(signal),
    enabled,
  });
}

export function useMarkRead() {
  const queryClient = useQueryClient();

  return useMutation<void, ApiError, string>({
    mutationFn: (id) => apiClient.markNotificationRead(id),
    // Optimistic: the row is read the moment the user has seen it. The server row is
    // the truth — a failed POST is healed by the invalidation below, not retried.
    onMutate: async (id) => {
      await queryClient.cancelQueries({ queryKey: notificationsKey });
      queryClient.setQueryData<Notification[]>(notificationsKey, (old) =>
        old?.map((n) => (n.id === id ? { ...n, isRead: true } : n)),
      );
    },
    onError: () => queryClient.invalidateQueries({ queryKey: notificationsKey }),
  });
}
```

- [ ] **Step 3: Write the failing stream test**

`useNotificationStream.test.ts` — the signalr mock shape is copied from `usePriceStream.test.ts`; the new part is asserting the cache patch:

```ts
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { Notification } from '@marketpulse/api-client';
import { renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { createElement } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { notificationsKey } from './useNotifications';
import { useNotificationStream } from './useNotificationStream';

const handlers = new Map<string, (payload: unknown) => void>();

const mockConnection = {
  on: vi.fn((event: string, handler: (payload: unknown) => void) => {
    handlers.set(event, handler);
  }),
  onreconnecting: vi.fn(),
  onreconnected: vi.fn(),
  onclose: vi.fn(),
  start: vi.fn().mockResolvedValue(undefined),
  stop: vi.fn().mockResolvedValue(undefined),
  state: 'Disconnected',
};

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: vi.fn().mockImplementation(() => ({
    withUrl: vi.fn().mockReturnThis(),
    withAutomaticReconnect: vi.fn().mockReturnThis(),
    configureLogging: vi.fn().mockReturnThis(),
    build: vi.fn(() => mockConnection),
  })),
  HubConnectionState: { Disconnected: 'Disconnected' },
  LogLevel: { Warning: 0 },
}));

const push = {
  id: 'n1',
  alertRuleId: 'a1',
  ticker: 'IVV',
  direction: 'Above',
  threshold: 60,
  triggeredPrice: 61.2,
  occurredUtc: '2026-08-05T00:00:00+00:00',
};

describe('useNotificationStream', () => {
  afterEach(() => {
    vi.clearAllMocks();
    handlers.clear();
  });

  function renderStream() {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    client.setQueryData<Notification[]>(notificationsKey, []);

    renderHook(() => useNotificationStream(true), {
      wrapper: ({ children }: { children: ReactNode }) =>
        createElement(QueryClientProvider, { client }, children),
    });

    return client;
  }

  it('prepends a pushed notification into the cache as unread', async () => {
    const client = renderStream();

    await waitFor(() => expect(handlers.has('notification')).toBe(true));
    handlers.get('notification')!(push);

    const cached = client.getQueryData<Notification[]>(notificationsKey);
    expect(cached?.[0]).toMatchObject({ id: 'n1', isRead: false });
  });

  it('ignores a payload that does not parse', async () => {
    const client = renderStream();

    await waitFor(() => expect(handlers.has('notification')).toBe(true));
    handlers.get('notification')!({ nonsense: true });

    expect(client.getQueryData<Notification[]>(notificationsKey)).toEqual([]);
  });

  it('does not open a connection when disabled', () => {
    const client = new QueryClient();

    renderHook(() => useNotificationStream(false), {
      wrapper: ({ children }: { children: ReactNode }) =>
        createElement(QueryClientProvider, { client }, children),
    });

    expect(mockConnection.start).not.toHaveBeenCalled();
  });
});
```

- [ ] **Step 4: Run to verify failure**

Run: `pnpm --filter @marketpulse/dashboard test -- --run src/features/notifications`
Expected: FAIL — hook does not exist.

- [ ] **Step 5: Implement `useNotificationStream.ts`**

```ts
import { notificationPushSchema, type Notification } from '@marketpulse/api-client';
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import { API_BASE_URL } from '../../api';
import { alertsKey } from '../alerts/useAlerts';
import { infiniteReconnectPolicy } from '../realtime/reconnectPolicy';
import { notificationsKey } from './useNotifications';

/**
 * Live notifications patched straight into the TanStack Query cache. The push is the
 * optimisation and the server row is the truth: a reconnect invalidates the query, so
 * anything missed while disconnected converges on the next refetch. This is the whole
 * client-state story — see ADR-007.
 */
export function useNotificationStream(enabled: boolean) {
  const queryClient = useQueryClient();

  useEffect(() => {
    if (!enabled) return;

    const connection = new HubConnectionBuilder()
      .withUrl(`${API_BASE_URL}/hubs/notifications`)
      .withAutomaticReconnect(infiniteReconnectPolicy)
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on('notification', (payload: unknown) => {
      const parsed = notificationPushSchema.safeParse(payload);
      if (!parsed.success) return;

      const notification: Notification = { ...parsed.data, isRead: false };

      queryClient.setQueryData<Notification[]>(notificationsKey, (old) =>
        old
          ? [notification, ...old.filter((n) => n.id !== notification.id)]
          : [notification],
      );

      // A notification means some rule just flipped to Triggered.
      void queryClient.invalidateQueries({ queryKey: alertsKey });
    });

    // Whatever was pushed while disconnected is already a row — refetch converges.
    connection.onreconnected(() => {
      void queryClient.invalidateQueries({ queryKey: notificationsKey });
    });

    connection.start().catch(() => {
      // withAutomaticReconnect owns retries; a failed initial start is retried by the
      // next mount. Notifications have no dedicated "reconnecting" UI — the price
      // stream's indicator already reports realtime health.
    });

    return () => {
      if (connection.state !== HubConnectionState.Disconnected) {
        void connection.stop();
      }
    };
  }, [enabled, queryClient]);
}
```

Note: `withAutomaticReconnect` only engages after a successful start; a *failed initial* start is not retried here. That is the same trade `usePriceStream` makes minus its status reducer, and the query refetch still shows rows. Do not add a retry loop.

- [ ] **Step 6: Write the failing bell test**

`NotificationBell.test.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { NotificationBell } from './NotificationBell';

// The stream is unit-tested on its own; here it must simply not open sockets.
vi.mock('./useNotificationStream', () => ({ useNotificationStream: () => undefined }));

const unread = {
  id: 'n1',
  alertRuleId: 'a1',
  ticker: 'IVV',
  direction: 'Above',
  threshold: 60,
  triggeredPrice: 61.2,
  occurredUtc: '2026-08-05T00:00:00+00:00',
  isRead: false,
};

const read = { ...unread, id: 'n2', isRead: true };

const server = setupServer(
  http.get('http://localhost:5100/api/v1/auth/me', () =>
    HttpResponse.json({ id: 'u1', email: 'billy@example.test' }),
  ),
  http.get('http://localhost:5100/api/v1/notifications', () =>
    HttpResponse.json([unread, read]),
  ),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

function renderBell() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <NotificationBell />
    </QueryClientProvider>,
  );
}

describe('NotificationBell', () => {
  it('shows the unread count, not the total', async () => {
    renderBell();

    const bell = await screen.findByRole('button', { name: /notifications/i });
    await waitFor(() => expect(bell).toHaveTextContent('1'));
  });

  it('opens the panel and marks the unread rows read', async () => {
    const readIds: string[] = [];
    server.use(
      http.post('http://localhost:5100/api/v1/notifications/:id/read', ({ params }) => {
        readIds.push(String(params['id']));
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderBell();

    await userEvent.click(await screen.findByRole('button', { name: /notifications/i }));

    expect(
      await screen.findByText('IVV crossed above $60.00 — $61.20'),
    ).toBeInTheDocument();

    // Only the unread row is posted; the read one is left alone. And the badge clears
    // optimistically — the unread count is derived from the cache, never stored.
    await waitFor(() => expect(readIds).toEqual(['n1']));
    await waitFor(() =>
      expect(screen.getByRole('button', { name: /notifications/i })).not.toHaveTextContent('1'),
    );
  });

  it('renders nothing without a session', async () => {
    server.use(
      http.get('http://localhost:5100/api/v1/auth/me', () =>
        HttpResponse.json({ title: 'unauthorized', status: 401 }, { status: 401 }),
      ),
    );

    const { container } = renderBell();

    await waitFor(() => expect(container).toBeEmptyDOMElement());
  });
});
```

- [ ] **Step 7: Implement bell and panel**

`NotificationsPanel.tsx`:

```tsx
import type { Notification } from '@marketpulse/api-client';
import { Panel } from '@marketpulse/ui';
import { useEffect } from 'react';
import styles from './Notifications.module.css';
import { useMarkRead } from './useNotifications';

export interface NotificationsPanelProps {
  notifications: Notification[];
}

export function NotificationsPanel({ notifications }: NotificationsPanelProps) {
  const markRead = useMarkRead();

  // Seen is read: opening the panel is the acknowledgement. Mount-only on purpose —
  // a row that arrives while the panel is already open stays unread until reopen,
  // which is also what keeps this from re-posting on every cache change.
  useEffect(() => {
    for (const n of notifications.filter((n) => !n.isRead)) {
      markRead.mutate(n.id);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <Panel className={styles.panel}>
      <h2 className={styles.heading}>Notifications</h2>
      {notifications.length === 0 ? (
        <p className={styles.empty}>Nothing yet. Alerts you set will land here when they fire.</p>
      ) : (
        <ul className={styles.list}>
          {notifications.map((n) => (
            <li key={n.id} className={styles.item} data-read={n.isRead}>
              <span>
                {`${n.ticker} crossed ${n.direction.toLowerCase()} $${n.threshold.toFixed(2)} — $${n.triggeredPrice.toFixed(2)}`}
              </span>
              <time className={styles.time} dateTime={n.occurredUtc}>
                {new Date(n.occurredUtc).toLocaleTimeString()}
              </time>
            </li>
          ))}
        </ul>
      )}
    </Panel>
  );
}
```

`NotificationBell.tsx`:

```tsx
import { Badge, Button } from '@marketpulse/ui';
import { useState } from 'react';
import { useSession } from '../auth/useSession';
import styles from './Notifications.module.css';
import { NotificationsPanel } from './NotificationsPanel';
import { useNotifications } from './useNotifications';
import { useNotificationStream } from './useNotificationStream';

export function NotificationBell() {
  const { data: session } = useSession();
  const [open, setOpen] = useState(false);
  const signedIn = Boolean(session);

  const { data: notifications } = useNotifications(signedIn);
  useNotificationStream(signedIn);

  if (!session) return null;

  const unread = notifications?.filter((n) => !n.isRead).length ?? 0;

  return (
    <div className={styles.bell}>
      <Button
        variant="ghost"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
      >
        Notifications <Badge count={unread} label="unread notifications" />
      </Button>
      {open && <NotificationsPanel notifications={notifications ?? []} />}
    </div>
  );
}
```

`Notifications.module.css`:

```css
.bell {
  position: relative;
}

.panel {
  position: absolute;
  right: 0;
  top: calc(100% + var(--mp-space-2));
  width: min(24rem, 90vw);
  max-height: 24rem;
  overflow-y: auto;
  z-index: 10;
  padding: var(--mp-space-3);
}

.heading {
  margin: 0 0 var(--mp-space-2);
  font-size: 1rem;
}

.empty {
  margin: 0;
  color: var(--mp-text-muted);
}

.list {
  margin: 0;
  padding: 0;
  list-style: none;
}

.item {
  display: flex;
  justify-content: space-between;
  gap: var(--mp-space-2);
  padding: var(--mp-space-2) 0;
  border-top: 1px solid var(--mp-border-subtle);
}

.item[data-read='true'] {
  color: var(--mp-text-muted);
}

.time {
  color: var(--mp-text-muted);
  font-size: 0.8125rem;
  white-space: nowrap;
}
```

Wire into `App.tsx` — the bell sits with the session controls:

```tsx
import { NotificationBell } from './features/notifications/NotificationBell';
```

```tsx
          <header className={styles.header}>
            <h1 className={styles.wordmark}>MarketPulse Pro</h1>
            <NotificationBell />
            <SignOutButton />
          </header>
```

If the header's flex layout pushes the bell awkwardly (SignOutButton expects to be the right-most flex child), wrap bell + sign-out in a flex `div` using an existing `app.module.css` pattern rather than restyling the header.

- [ ] **Step 8: Run the dashboard suite**

Run: `pnpm --filter @marketpulse/dashboard test -- --run && pnpm --filter @marketpulse/dashboard build`
Expected: PASS. (If `LoginScreen`/`ProtectedRoute` tests fail on unhandled `/api/v1/notifications` requests, they render screens rather than `App` — they should not need changes; if one does render `App`, give its msw server the `/auth/me` 401 handler it already has plus an empty notifications handler.)

- [ ] **Step 9: Commit**

```bash
git add apps/dashboard/src
git commit -m "feat(dashboard): notification bell, panel, and live unread badge"
```

---

### Task 7: The chaos test

Real API host + real worker services, Testcontainers SQL Server + RabbitMQ. Deviation from the spec's step-3 wording, recorded here deliberately: `OutboxDispatcher.PollInterval` is a hardcoded 500 ms, and its `DispatchPendingAsync` is already the public seam `OutboxDispatchTests` drives. Rather than make the interval configurable for the test's sake, the test hosts `PriceConsumer` (so evaluation is live) and drives dispatch **by hand** — which holds the Triggered→dispatched window open for exactly as long as the test needs to kill the broker inside it. Same claim proved, zero production changes, no timing coin-flips.

**Files:**
- Modify: `tests/MarketPulse.IntegrationTests/RabbitMqFixture.cs` (stop/start passthroughs)
- Create: `tests/MarketPulse.IntegrationTests/ChaosTests.cs`

**Interfaces:**
- Consumes: `TestFactory.Create(sql, overrides)`, `AuthenticatedClient.RegisterAsync(factory)`, `SqlServerFixture.CreateContext()`, `MessagingCollection`, `OutboxDispatcher.DispatchPendingAsync(ct)`, `AddPersistence(connectionString)`/`AddMessaging()` service registrations, `AlertEvaluator`, `PriceConsumer`.
- Produces: `RabbitMqFixture.StopBrokerAsync()` / `StartBrokerAsync()`.

- [ ] **Step 1: Add the fixture passthroughs**

In `RabbitMqFixture`:

```csharp
    /// <summary>
    /// The chaos test's kill switch. Stop/start (not dispose/recreate) keeps the same
    /// container, so the port mapping and the durable queues survive the outage — the
    /// broker "comes back" the way a restarted production node would.
    /// </summary>
    public Task StopBrokerAsync() => _container.StopAsync();

    public Task StartBrokerAsync() => _container.StartAsync();
```

- [ ] **Step 2: Write the chaos test**

`ChaosTests.cs`. It lives in `MessagingCollection`, which is safe precisely because xUnit runs classes in one collection sequentially — nobody else is using the broker while this class stops it (and it restarts the broker before finishing, pass or fail).

```csharp
using System.Net;
using System.Net.Http.Json;
using MarketPulse.Alerts;
using MarketPulse.Application.Configuration;
using MarketPulse.Infrastructure;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarketPulse.IntegrationTests;

/// <summary>
/// The README's headline claim, demonstrated rather than asserted: an alert that fires
/// while RabbitMQ is down is not lost. Real API host (rule CRUD, tick feed into the
/// broker, AlertTriggeredConsumer writing notification rows), real worker services
/// (PriceConsumer + AlertEvaluator hosted exactly as MarketPulse.Alerts' Program.cs
/// hosts them), and a real broker container stopped mid-flow.
///
/// <para>Outbox dispatch is driven by hand through the same public seam
/// OutboxDispatchTests uses. That is what makes the interesting window — rule marked
/// Triggered, outbox row committed, nothing dispatched yet — a place this test stands
/// still in, rather than a 500 ms slot it races the hosted dispatcher for.</para>
/// </summary>
[Collection(nameof(MessagingCollection))]
public class ChaosTests(SqlServerFixture sql, RabbitMqFixture rabbit)
{
    private sealed record AlertRuleResponse(Guid Id, string Ticker, string Status);

    private sealed record NotificationResponse(Guid Id, string Ticker, decimal TriggeredPrice);

    private Dictionary<string, string?> BrokerSettings()
    {
        var uri = new Uri(rabbit.ConnectionString);

        return new Dictionary<string, string?>
        {
            ["RabbitMq:HostName"] = uri.Host,
            ["RabbitMq:Port"] = uri.Port.ToString(),
            ["RabbitMq:UserName"] = uri.UserInfo.Split(':')[0],
            ["RabbitMq:Password"] = uri.UserInfo.Split(':')[1],

            // Keeps every reconnect loop brisk after the restart. The production cap of
            // 30s is for real outages; this test is not asserting anything about backoff.
            ["RabbitMq:MaxConnectionRetryDelay"] = "00:00:02"
        };
    }

    /// <summary>The worker composed as Program.cs composes it, minus the hosted dispatcher.</summary>
    private IHost BuildWorker()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Configuration.AddInMemoryCollection(BrokerSettings());

        builder.Services.AddOptions<RabbitMqOptions>()
            .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName));

        builder.Services.AddPersistence(sql.ConnectionString);
        builder.Services.AddMessaging();
        builder.Services.AddScoped<AlertEvaluator>();
        builder.Services.AddHostedService<PriceConsumer>();

        return builder.Build();
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition, string what, int seconds = 60)
    {
        for (var i = 0; i < seconds * 2; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(500);
        }

        Assert.Fail($"Timed out after {seconds}s waiting for {what}.");
    }

    /// <summary>One dispatch attempt, bounded so a wedged broker call cannot hang the test.</summary>
    private static async Task<int> TryDispatchAsync(OutboxDispatcher dispatcher)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            return await dispatcher.DispatchPendingAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            // A broker that is down answers with whatever exception it likes; every one
            // of them means "nothing was confirmed", which is all the caller needs.
            return 0;
        }
    }

    [Fact]
    public async Task An_alert_that_fires_while_the_broker_is_down_is_delivered_exactly_once_after_it_returns()
    {
        await using var factory = TestFactory.Create(sql, BrokerSettings());
        var client = await AuthenticatedClient.RegisterAsync(factory);

        using var worker = BuildWorker();
        await worker.StartAsync();

        try
        {
            // An Above rule at one cent: the API's fake feed prices all sit far above it,
            // so the very next tick through the real broker trips the rule. One-shot
            // semantics mean exactly one trigger no matter how many ticks follow.
            var created = await client.PostAsJsonAsync(
                "/api/v1/alerts",
                new { Ticker = "IVV", Direction = "Above", Threshold = 0.01m });
            var rule = await created.Content.ReadFromJsonAsync<AlertRuleResponse>();
            Assert.NotNull(rule);

            // The rule flip and its outbox row commit in one unit of work, so "Triggered"
            // means the row is already there — and with no dispatcher hosted, it stays
            // pending for as long as this test likes. The window is now open.
            await WaitUntilAsync(async () =>
            {
                await using var db = sql.CreateContext();
                return await db.AlertRules
                    .AnyAsync(r => r.Id == rule!.Id && r.Status == AlertRuleStatus.Triggered);
            }, "the rule to trigger off the live feed");

            Guid pendingId;
            await using (var db = sql.CreateContext())
            {
                pendingId = await db.OutboxMessages
                    .Where(m => m.DispatchedUtc == null
                                && m.Payload.Contains(rule!.Id.ToString()))
                    .Select(m => m.Id)
                    .SingleAsync();
            }

            // ---- The outage. ----
            await rabbit.StopBrokerAsync();

            var dispatcher = new OutboxDispatcher(
                worker.Services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<OutboxDispatcher>.Instance);

            // Dispatch attempted against a dead broker: nothing is confirmed, nothing is
            // marked dispatched, and nothing crashes.
            Assert.Equal(0, await TryDispatchAsync(dispatcher));

            await using (var db = sql.CreateContext())
            {
                var stillPending = await db.OutboxMessages
                    .SingleAsync(m => m.Id == pendingId);
                Assert.Null(stillPending.DispatchedUtc);

                Assert.Equal(0, await db.Notifications.CountAsync(
                    n => n.AlertRuleId == rule!.Id));
            }

            // The API is alive throughout — the broker being down must not take it down.
            Assert.Equal(
                HttpStatusCode.OK,
                (await factory.CreateClient().GetAsync("/health")).StatusCode);

            // ---- The broker returns. ----
            await rabbit.StartBrokerAsync();

            // Connection recovery is the client library's job and takes a few seconds;
            // retry dispatch until exactly one row is confirmed through.
            var dispatched = 0;
            await WaitUntilAsync(async () =>
            {
                dispatched += await TryDispatchAsync(dispatcher);
                return dispatched >= 1;
            }, "the outbox row to dispatch after the broker returned");

            Assert.Equal(1, dispatched);

            // And the API's consumer — which lost its channel in the outage and
            // resubscribed through its supervision loop — lands the row exactly once.
            await WaitUntilAsync(async () =>
            {
                var list = await client.GetFromJsonAsync<List<NotificationResponse>>(
                    "/api/v1/notifications");
                return list?.Count == 1;
            }, "the notification row to arrive via the API's consumer");

            // Grace period so a wrongly duplicated delivery has time to land and fail this.
            await Task.Delay(TimeSpan.FromSeconds(2));

            Assert.Single((await client.GetFromJsonAsync<List<NotificationResponse>>(
                "/api/v1/notifications"))!);

            await using (var finalDb = sql.CreateContext())
            {
                var row = await finalDb.OutboxMessages.SingleAsync(m => m.Id == pendingId);
                Assert.NotNull(row.DispatchedUtc);
            }
        }
        finally
        {
            // The broker must be back before the next class in this collection runs,
            // even when an assertion above has already failed.
            await rabbit.StartBrokerAsync();

            var stop = worker.StopAsync();
            Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(15))));
        }
    }
}
```

Adjust names to reality while implementing (the test must compile against the actual model, not this plan): the `AlertRuleStatus` enum's namespace (`MarketPulse.Domain.Entities` or similar — check `AlertRule.cs`), the `db.AlertRules`/`db.Notifications`/`db.OutboxMessages` DbSet names (check `MarketPulseDbContext`), and `Notification.AlertRuleId` property casing. If `AuthenticatedClient.RegisterAsync(factory)` requires an email argument, use `AuthenticatedClient.NewEmail()` as `AlertPipelineTests` does. Keep the assertions exactly as specified.

- [ ] **Step 3: Run it**

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter ChaosTests`
Expected: PASS in roughly 1–2 minutes (container stop/start dominates). Then run the whole messaging collection to prove the shared-broker choreography holds: `dotnet test tests/MarketPulse.IntegrationTests`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add tests/MarketPulse.IntegrationTests
git commit -m "test(chaos): broker killed mid-flow, zero lost alerts, exactly one delivery"
```

---

### Task 8: Playwright journey and e2e harness

**Files:**
- Create: `tests/e2e/global-setup.ts`
- Create: `tests/e2e/global-teardown.ts`
- Modify: `tests/e2e/playwright.config.ts`
- Create: `tests/e2e/specs/alerts.spec.ts`

**Interfaces:**
- Consumes: UI accessible names from Tasks 5–6 (`Alert direction for IVV`, `Alert threshold for IVV`, `Set alert for IVV`, bell button named `/notifications/i`, panel heading `Notifications`, row copy `IVV crossed above $…`); worker `dotnet run` config from `src/MarketPulse.Alerts/appsettings.json` (docker-compose SQL + broker on localhost defaults).
- Produces: a worker process alive for the whole e2e run, killed in teardown.

- [ ] **Step 1: Harness — global setup/teardown**

`global-setup.ts`:

```ts
import { spawn } from 'node:child_process';
import { writeFile } from 'node:fs/promises';
import path from 'node:path';

export const WORKER_PID_FILE = path.join(__dirname, '.alerts-worker.pid');

/**
 * The Alerts worker exposes no HTTP port, so it cannot join webServer[]. It is spawned
 * detached (its own process group, so teardown can kill `dotnet run`'s whole tree) and
 * given no readiness probe: the spec's first live assertion — a rule flipping to
 * Triggered — is the worker's readiness, with the same generous timeout the API gets.
 */
export default async function globalSetup() {
  const worker = spawn('dotnet', ['run', '--project', '../../src/MarketPulse.Alerts'], {
    cwd: __dirname,
    stdio: 'ignore',
    detached: true,
  });
  worker.unref();

  if (worker.pid === undefined) {
    throw new Error('Failed to spawn the Alerts worker.');
  }

  await writeFile(WORKER_PID_FILE, String(worker.pid));
}
```

`global-teardown.ts`:

```ts
import { readFile, rm } from 'node:fs/promises';
import { WORKER_PID_FILE } from './global-setup';

export default async function globalTeardown() {
  try {
    const pid = Number(await readFile(WORKER_PID_FILE, 'utf8'));
    // Negative pid: the detached process group, so the built app dies with `dotnet run`.
    process.kill(-pid, 'SIGTERM');
  } catch {
    // Already gone (or never started) — nothing to clean up.
  } finally {
    await rm(WORKER_PID_FILE, { force: true });
  }
}
```

In `playwright.config.ts`, register both beside `testDir`:

```ts
  globalSetup: './global-setup.ts',
  globalTeardown: './global-teardown.ts',
```

Add `.alerts-worker.pid` to `tests/e2e/.gitignore` (create the file if the directory has none; check for an existing ignore entry pattern first).

- [ ] **Step 2: The journey**

`specs/alerts.spec.ts`:

```ts
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

  await bell.click();
  await expect(page.getByRole('heading', { name: 'Notifications' })).toBeVisible();
  await expect(
    page.getByText(new RegExp(`IVV crossed above \\$${threshold.replace('.', '\\.')}`)),
  ).toBeVisible();

  // Opening was the acknowledgement: the badge clears, and stays cleared across a full
  // reload because read state lives on the server, not in the client.
  await expect(bell).not.toContainText('1');
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Watchlist' })).toBeVisible();
  await expect(page.getByRole('button', { name: /notifications/i })).not.toContainText('1');
});
```

- [ ] **Step 3: Run the e2e suite**

Prerequisites: `docker compose up -d` (SQL Server **and** RabbitMQ healthy — the compose file already has both).

Run: `pnpm --dir tests/e2e exec playwright test`
Expected: PASS — `authentication.spec.ts` (unchanged) and the new journey. If the journey flakes on the `Triggered` step, the worker is the suspect: check it actually started (`dotnet run` builds first; give it one manual run to warm the build before judging).

- [ ] **Step 4: Commit**

```bash
git add tests/e2e
git commit -m "test(e2e): alert-to-notification journey with the worker in the harness"
```

---

### Task 9: ADR-007, README correction, ROADMAP and TESTING updates

**Files:**
- Create: `docs/adr/007-state-architecture.md`
- Modify: `README.md` and/or `docs/MarketPulse-Pro-README.md` — wherever `grep -rn "Zustand" README.md docs/` matches
- Modify: `docs/ROADMAP.md`
- Modify: `docs/TESTING.md`

- [ ] **Step 1: Write ADR-007**

Follow the house ADR shape (read `docs/adr/009-messaging-architecture.md` first and mirror its section headings — likely Status/Context/Decision/Consequences with rejected alternatives). Content it must carry:

- **Decision:** client state is the server cache. TanStack Query holds every piece of server truth; SignalR pushes are optimisations patched into that cache (`useNotificationStream` prepends, reconnect invalidates); derived values (the unread badge) are computed from the cache at render, never stored; ephemeral view state (a panel's open flag) is `useState` and beneath architecture.
- **Rejected: Zustand** — the README promised a "TanStack Query + Zustand" two-layer client. The only candidate for the second layer, unread state, turned out to be server state in disguise: the server already owns `IsRead` per row and exposes the mutation. A client store would have been a second copy of data with an invalidation problem, added to make the codebase match a document. The document moved instead.
- **Trigger to revisit:** genuinely client-owned cross-component state that no server row backs (e.g. a multi-step form wizard, optimistic UI beyond a boolean flip). Name the condition concretely so slice 5+ can test against it.
- Cross-reference the 4b spec and the roadmap's "no Zustand merely to satisfy ADR-007" warning.

- [ ] **Step 2: Correct the README claim**

`grep -rn "Zustand" README.md docs/` — rewrite each hit so the claim reads "TanStack Query as the single client-side state layer; live data patched into the query cache over SignalR (ADR-007)". Do not leave any sentence promising a state library that is not a dependency.

- [ ] **Step 3: Update ROADMAP.md**

- Phase-status table: "3 · Messaging & alerts" → **Done** (chaos test landed); "4 · Frontend core" → remove "alerts and notifications UI" from its gap list and resolve the client-state parenthetical to "ADR-007 written: server cache only".
- `### 4b` section: mark done in whatever style earlier completed slices use, noting the two carried-over fixes landed here and the remaining three findings' new home (observability slice for the requeue loop; the other two remain recorded where they are).
- The `docs/adr/007-state-architecture.md` row in the debt table: point it at the now-written ADR.
- The 4a "manual done-criteria never rehearsed" paragraph: note the chaos test now automates it.

- [ ] **Step 4: Update TESTING.md**

Add the chaos test to the integration-test inventory (what it composes, what it proves, that dispatch is hand-driven through the public seam) and the e2e harness's new shape (worker via global-setup, RabbitMQ as a compose prerequisite for e2e).

- [ ] **Step 5: Commit**

```bash
git add docs README.md
git commit -m "docs: ADR-007 server-cache state architecture; reconcile README, roadmap, testing"
```

---

### Task 10: Full verification

- [ ] **Step 1: Clean-slate build and test, every suite**

```bash
pnpm -r build
pnpm -r test -- --run
dotnet build MarketPulse.sln
dotnet test
docker compose up -d && pnpm --dir tests/e2e exec playwright test
```

Expected: everything green. `dotnet test` includes the chaos test (Docker must be running).

- [ ] **Step 2: Spec done-criteria walkthrough**

Check each of the spec's six done criteria against evidence from Step 1's output (use superpowers:verification-before-completion — claims need command output, not recollection). Confirm `DependencyRuleTests` ran and passed.

- [ ] **Step 3: Finish the branch**

Use superpowers:finishing-a-development-branch. Per the repo's promotion flow: merge `feature/slice-4b-alerts-ui-chaos-test` into `test` (no Co-Authored-By trailer anywhere in the branch — verify with `git log --format=%B origin/test..HEAD | grep -i co-authored` returning nothing).
