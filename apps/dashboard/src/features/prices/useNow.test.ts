import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useNow } from './useNow';

describe('useNow', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('returns the current time on first render', () => {
    const start = Date.now();
    const { result } = renderHook(() => useNow());

    expect(result.current).toBe(start);
  });

  it('advances on its own, without any external re-render trigger', () => {
    const start = Date.now();
    const { result } = renderHook(() => useNow());

    act(() => {
      vi.advanceTimersByTime(1000);
    });

    expect(result.current).toBe(start + 1000);

    act(() => {
      vi.advanceTimersByTime(9000);
    });

    expect(result.current).toBe(start + 10_000);
  });

  it('stops ticking after unmount', () => {
    const start = Date.now();
    const { result, unmount } = renderHook(() => useNow());

    unmount();

    act(() => {
      vi.advanceTimersByTime(5000);
    });

    expect(result.current).toBe(start);
  });
});
