import { z } from 'zod';

export const watchlistItemSchema = z.object({
  ticker: z.string().min(1).max(8),
  addedUtc: z.string(),
});

export const watchlistSchema = z.object({
  id: z.string(),
  items: z.array(watchlistItemSchema),
});

export const tickSchema = z.object({
  ticker: z.string().min(1).max(8),
  price: z.number().positive(),
  timestampUtc: z.string(),
});

export const problemDetailsSchema = z.object({
  title: z.string(),
  status: z.number(),
  detail: z.string().optional(),
  correlationId: z.string().optional(),
});

export type WatchlistItem = z.infer<typeof watchlistItemSchema>;
export type Watchlist = z.infer<typeof watchlistSchema>;
export type Tick = z.infer<typeof tickSchema>;
export type ProblemDetails = z.infer<typeof problemDetailsSchema>;
