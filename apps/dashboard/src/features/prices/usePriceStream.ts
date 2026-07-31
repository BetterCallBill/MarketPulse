import { tickSchema } from '@marketpulse/api-client';
import type { IRetryPolicy, RetryContext } from '@microsoft/signalr';
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { useEffect, useReducer } from 'react';
import { API_BASE_URL } from '../../api';
import { initialStreamState, streamReducer } from './streamReducer';

// Ramp through these delays for the first few attempts, then retry forever at the
// final, capped delay. `withAutomaticReconnect([...])` alone stops retrying once the
// array is exhausted, leaving the UI stuck in "reconnecting" forever — see Important 5.
const RECONNECT_DELAYS_MS = [0, 2000, 5000, 10_000, 30_000];

const FINAL_RECONNECT_DELAY_MS = RECONNECT_DELAYS_MS[RECONNECT_DELAYS_MS.length - 1] ?? 30_000;

const infiniteReconnectPolicy: IRetryPolicy = {
  nextRetryDelayInMilliseconds(retryContext: RetryContext): number {
    const index = retryContext.previousRetryCount;
    return index < RECONNECT_DELAYS_MS.length
      ? (RECONNECT_DELAYS_MS[index] ?? FINAL_RECONNECT_DELAY_MS)
      : FINAL_RECONNECT_DELAY_MS;
  },
};

export function usePriceStream() {
  const [state, dispatch] = useReducer(streamReducer, initialStreamState);

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl(`${API_BASE_URL}/hubs/prices`)
      .withAutomaticReconnect(infiniteReconnectPolicy)
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on('tick', (payload: unknown) => {
      const parsed = tickSchema.safeParse(payload);
      if (!parsed.success) return;

      dispatch({
        type: 'tick',
        ticker: parsed.data.ticker,
        price: parsed.data.price,
        receivedAt: Date.now(),
      });
    });

    connection.onreconnecting(() => dispatch({ type: 'reconnecting' }));
    connection.onreconnected(() => dispatch({ type: 'connected' }));
    connection.onclose(() => dispatch({ type: 'reconnecting' }));

    connection
      .start()
      .then(() => dispatch({ type: 'connected' }))
      .catch(() => dispatch({ type: 'reconnecting' }));

    return () => {
      if (connection.state !== HubConnectionState.Disconnected) {
        void connection.stop();
      }
    };
  }, []);

  return state;
}
