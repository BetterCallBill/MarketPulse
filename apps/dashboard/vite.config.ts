import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [react()],
  server: { port: 5173 },
  test: {
    environment: './src/test/jsdomNodeFetchEnvironment.ts',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
  },
});
