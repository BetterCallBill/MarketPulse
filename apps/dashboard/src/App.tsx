import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { BrowserRouter, Route, Routes } from 'react-router-dom';
import { Suspense, lazy } from 'react';
import styles from './app.module.css';
import { LoginScreen } from './features/auth/LoginScreen';
import { ProtectedRoute } from './features/auth/ProtectedRoute';
import { RegisterScreen } from './features/auth/RegisterScreen';
import { SignOutButton } from './features/auth/SignOutButton';
import { NotificationBell } from './features/notifications/NotificationBell';
import { Nav } from './features/nav/Nav';
import { PortfolioScreen } from './features/portfolio/PortfolioScreen';
import { WatchlistScreen } from './features/watchlist/WatchlistScreen';

// The chart library rides this chunk and no other — the README's route-based
// code-splitting promise, verified against the build output in this slice.
const PriceHistoryScreen = lazy(() => import('./features/history/PriceHistoryScreen'));

const queryClient = new QueryClient();

export function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <div className={styles.page}>
          <header className={styles.header}>
            <h1 className={styles.wordmark}>MarketPulse Pro</h1>
            <Nav />
            <div className={styles.controls}>
              <NotificationBell />
              <SignOutButton />
            </div>
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
              <Route
                path="/portfolio"
                element={
                  <ProtectedRoute>
                    <PortfolioScreen />
                  </ProtectedRoute>
                }
              />
              <Route
                path="/prices/:ticker"
                element={
                  <ProtectedRoute>
                    <Suspense fallback={<p role="status">Loading chart…</p>}>
                      <PriceHistoryScreen />
                    </Suspense>
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
