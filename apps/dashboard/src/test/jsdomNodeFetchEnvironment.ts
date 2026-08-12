import { builtinEnvironments, type Environment } from 'vitest/environments';

/**
 * Wraps the built-in `jsdom` environment and restores Node's native
 * `AbortController`/`AbortSignal` after jsdom installs its own globals.
 *
 * On Node 24, the global `fetch` (undici) rejects an `AbortSignal` that
 * wasn't constructed by its own internal class. Vitest's jsdom environment
 * shadows `globalThis.AbortController`/`AbortSignal` with jsdom's
 * implementation, so any code that does `new AbortController()` (e.g.
 * TanStack Query's built-in request cancellation) and forwards `.signal`
 * to `fetch` hits: "TypeError: RequestInit: Expected signal to be an
 * instance of AbortSignal." See https://github.com/vitest-dev/vitest/issues/8374
 * (fixed upstream in Vitest 4; this project pins Vitest 2.x per spec).
 *
 * Each vitest test file runs with a fresh, unpolluted `globalThis`
 * (test isolation is on by default), so capturing the native classes at
 * module-evaluation time — before jsdom's `setup()` runs — is safe.
 */
const nativeAbortController = globalThis.AbortController;
const nativeAbortSignal = globalThis.AbortSignal;

const jsdomEnvironment = builtinEnvironments.jsdom;

export default {
  ...jsdomEnvironment,
  name: 'jsdom-node-fetch',
  async setup(global, options) {
    const result = await jsdomEnvironment.setup(global, options);
    global.AbortController = nativeAbortController;
    global.AbortSignal = nativeAbortSignal;
    return result;
  },
} satisfies Environment;
