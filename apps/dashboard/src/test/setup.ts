import '@testing-library/jest-dom/vitest';

// jsdom has no ResizeObserver; the chart component observes its container.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
globalThis.ResizeObserver ??= ResizeObserverStub as unknown as typeof ResizeObserver;
