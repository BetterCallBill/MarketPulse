import type { Candle } from '@marketpulse/api-client';
import {
  CandlestickSeries,
  createChart,
  type IChartApi,
  type ISeriesApi,
  type UTCTimestamp,
} from 'lightweight-charts';
import { useEffect, useRef } from 'react';
import styles from './CandleChart.module.css';

export interface CandleChartProps {
  candles: Candle[];
}

function toSeriesData(candles: Candle[]) {
  return candles.map((c) => ({
    time: (Date.parse(c.t) / 1000) as UTCTimestamp,
    open: c.o,
    high: c.h,
    low: c.l,
    close: c.c,
  }));
}

/**
 * The only file that imports lightweight-charts — it lives in the lazy route chunk and
 * nowhere else. Owns the full chart lifecycle in effects: create on mount, setData on
 * change, resize with the container, remove on cleanup. Written to survive StrictMode's
 * deliberate double-invoke: each mount creates its own chart and each cleanup removes it.
 */
export function CandleChart({ candles }: CandleChartProps) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const seriesRef = useRef<ISeriesApi<'Candlestick'> | null>(null);
  const chartRef = useRef<IChartApi | null>(null);
  const candlesRef = useRef(candles);
  candlesRef.current = candles;

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    // Canvas cannot read CSS custom properties; resolve the semantic tokens here so the
    // chart still follows the design system.
    const tokens = getComputedStyle(container);
    const up = tokens.getPropertyValue('--mp-price-up').trim() || '#4ade80';
    const down = tokens.getPropertyValue('--mp-price-down').trim() || '#f87171';
    const text = tokens.getPropertyValue('--mp-text-secondary').trim() || '#8b95a5';

    const chart = createChart(container, {
      height: 360,
      width: container.clientWidth,
      layout: { background: { color: 'transparent' }, textColor: text },
      grid: { vertLines: { visible: false }, horzLines: { visible: false } },
    });
    const series = chart.addSeries(CandlestickSeries, {
      upColor: up,
      downColor: down,
      wickUpColor: up,
      wickDownColor: down,
      borderVisible: false,
    });
    series.setData(toSeriesData(candlesRef.current));
    chart.timeScale().fitContent();

    const observer = new ResizeObserver(() => {
      chart.applyOptions({ width: container.clientWidth });
    });
    observer.observe(container);

    chartRef.current = chart;
    seriesRef.current = series;

    return () => {
      observer.disconnect();
      chart.remove();
      chartRef.current = null;
      seriesRef.current = null;
    };
  }, []);

  useEffect(() => {
    seriesRef.current?.setData(toSeriesData(candles));
    chartRef.current?.timeScale().fitContent();
  }, [candles]);

  return <div ref={containerRef} className={styles.chart} data-testid="candle-chart" />;
}
