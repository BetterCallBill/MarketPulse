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

export const sessionSchema = z.object({
  id: z.string(),
  email: z.string(),
});

export const alertDirectionSchema = z.enum(['Above', 'Below']);

export const alertRuleSchema = z.object({
  id: z.string(),
  ticker: z.string().min(1).max(8),
  direction: alertDirectionSchema,
  threshold: z.number().positive(),
  status: z.enum(['Active', 'Triggered']),
  createdUtc: z.string(),
  triggeredUtc: z.string().nullable(),
  triggeredPrice: z.number().nullable(),
});

export const notificationSchema = z.object({
  id: z.string(),
  alertRuleId: z.string(),
  ticker: z.string().min(1).max(8),
  direction: alertDirectionSchema,
  threshold: z.number(),
  triggeredPrice: z.number(),
  occurredUtc: z.string(),
  isRead: z.boolean(),
});

/** The hub payload is the notification minus its read flag — a push is unread by definition. */
export const notificationPushSchema = notificationSchema.omit({ isRead: true });

export const tradeSideSchema = z.enum(['Buy', 'Sell']);

export const holdingSchema = z.object({
  ticker: z.string().min(1).max(8),
  units: z.number(),
  averageCost: z.number(),
  realisedPnL: z.number(),
});

export const portfolioSchema = z.object({
  holdings: z.array(holdingSchema),
  totalRealisedPnL: z.number(),
});

export const transactionSchema = z.object({
  id: z.string(),
  ticker: z.string().min(1).max(8),
  side: tradeSideSchema,
  units: z.number().positive(),
  price: z.number().positive(),
  occurredUtc: z.string(),
  recordedUtc: z.string(),
});

export type WatchlistItem = z.infer<typeof watchlistItemSchema>;
export type Watchlist = z.infer<typeof watchlistSchema>;
export type Tick = z.infer<typeof tickSchema>;
export type ProblemDetails = z.infer<typeof problemDetailsSchema>;
export type Session = z.infer<typeof sessionSchema>;
export type AlertDirection = z.infer<typeof alertDirectionSchema>;
export type AlertRule = z.infer<typeof alertRuleSchema>;
export type Notification = z.infer<typeof notificationSchema>;
export type NotificationPush = z.infer<typeof notificationPushSchema>;
export type TradeSide = z.infer<typeof tradeSideSchema>;
export type Holding = z.infer<typeof holdingSchema>;
export type Portfolio = z.infer<typeof portfolioSchema>;
export type Transaction = z.infer<typeof transactionSchema>;
