import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { WatchlistScreen } from './features/watchlist/WatchlistScreen';

const queryClient = new QueryClient();

export function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <header>
        <h1>MarketPulse Pro</h1>
      </header>
      <main>
        <WatchlistScreen />
      </main>
    </QueryClientProvider>
  );
}
