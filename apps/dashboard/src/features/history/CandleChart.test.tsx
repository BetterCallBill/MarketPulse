import { render } from '@testing-library/react';
import { StrictMode } from 'react';
import { describe, expect, it, vi } from 'vitest';

const setData = vi.fn();
const remove = vi.fn();
const applyOptions = vi.fn();
const fitContent = vi.fn();
const createChart = vi.fn((..._args: unknown[]) => ({
  addSeries: vi.fn(() => ({ setData })),
  applyOptions,
  remove,
  timeScale: () => ({ fitContent }),
}));

vi.mock('lightweight-charts', () => ({
  createChart: (...args: unknown[]) => createChart(...args),
  CandlestickSeries: Symbol('CandlestickSeries'),
}));

import { CandleChart } from './CandleChart';

const CANDLES = [
  { t: '2026-08-06T10:00:00+00:00', o: 10, h: 12, l: 9, c: 11 },
  { t: '2026-08-06T10:01:00+00:00', o: 11, h: 13, l: 10, c: 12 },
];

describe('CandleChart', () => {
  it('hands converted candles to the series', () => {
    render(<CandleChart candles={CANDLES} />);

    expect(setData).toHaveBeenCalledWith([
      { time: Date.parse(CANDLES[0]!.t) / 1000, open: 10, high: 12, low: 9, close: 11 },
      { time: Date.parse(CANDLES[1]!.t) / 1000, open: 11, high: 13, low: 10, close: 12 },
    ]);
  });

  it('destroys the chart on unmount', () => {
    const { unmount } = render(<CandleChart candles={CANDLES} />);
    unmount();
    expect(remove).toHaveBeenCalled();
  });

  it('survives StrictMode double-invoke without leaking charts', () => {
    createChart.mockClear();
    remove.mockClear();
    const { unmount } = render(
      <StrictMode>
        <CandleChart candles={CANDLES} />
      </StrictMode>,
    );
    unmount();
    // however many charts were created, the same number were removed
    expect(remove).toHaveBeenCalledTimes(createChart.mock.calls.length);
  });
});
