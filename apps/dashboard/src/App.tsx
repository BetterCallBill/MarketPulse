import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { BrowserRouter, Route, Routes } from 'react-router-dom';
import { LoginScreen } from './features/auth/LoginScreen';
import { ProtectedRoute } from './features/auth/ProtectedRoute';
import { RegisterScreen } from './features/auth/RegisterScreen';
import { SignOutButton } from './features/auth/SignOutButton';
import { WatchlistScreen } from './features/watchlist/WatchlistScreen';

const queryClient = new QueryClient();

export function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <header>
          <h1>MarketPulse Pro</h1>
          <SignOutButton />
        </header>
        <main>
          <Routes>
            <Route path="/login" element={<LoginScreen />} />
            <Route path="/register" element={<RegisterScreen />} />
            <Route
              path="/"
              element={
                <ProtectedRoute>
                  <WatchlistScreen />
                </ProtectedRoute>
              }
            />
          </Routes>
        </main>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
