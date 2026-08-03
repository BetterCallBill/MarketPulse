import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { BrowserRouter, Route, Routes } from 'react-router-dom';
import styles from './app.module.css';
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
        <div className={styles.page}>
          <header className={styles.header}>
            <h1 className={styles.wordmark}>MarketPulse Pro</h1>
            <SignOutButton />
          </header>
          <main className={styles.main}>
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
        </div>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
