namespace MarketPulse.Domain.ValueObjects;

public readonly record struct PriceTick(string Ticker, decimal Price, DateTimeOffset TimestampUtc);
