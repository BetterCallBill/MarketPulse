import { useEffect, useState } from 'react';

/**
 * Returns the current time in epoch milliseconds, refreshed on an interval.
 *
 * Staleness dimming needs a live clock: if nothing else forces a re-render
 * (e.g. the feed silently stalls without the SignalR connection formally
 * closing), a `Date.now()` read taken during render stays frozen forever.
 * This hook ticks its own state so consumers re-render even when no new
 * price ticks arrive.
 */
export function useNow(intervalMs = 1000): number {
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(id);
  }, [intervalMs]);

  return now;
}
