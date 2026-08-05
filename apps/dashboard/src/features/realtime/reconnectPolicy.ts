import type { IRetryPolicy, RetryContext } from '@microsoft/signalr';

// Ramp through these delays for the first few attempts, then retry forever at the
// final, capped delay. `withAutomaticReconnect([...])` alone stops retrying once the
// array is exhausted, leaving the UI stuck in "reconnecting" forever — see Important 5.
const RECONNECT_DELAYS_MS = [0, 2000, 5000, 10_000, 30_000];

const FINAL_RECONNECT_DELAY_MS = RECONNECT_DELAYS_MS[RECONNECT_DELAYS_MS.length - 1] ?? 30_000;

export const infiniteReconnectPolicy: IRetryPolicy = {
  nextRetryDelayInMilliseconds(retryContext: RetryContext): number {
    const index = retryContext.previousRetryCount;
    return index < RECONNECT_DELAYS_MS.length
      ? (RECONNECT_DELAYS_MS[index] ?? FINAL_RECONNECT_DELAY_MS)
      : FINAL_RECONNECT_DELAY_MS;
  },
};
