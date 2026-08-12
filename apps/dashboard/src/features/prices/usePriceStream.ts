import { tickSchema } from '@marketpulse/api-client';
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { useEffect, useReducer } from 'react';
import { API_BASE_URL } from '../../api';
import { infiniteReconnectPolicy } from '../realtime/reconnectPolicy';
import { initialStreamState, streamReducer } from './streamReducer';

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
