namespace MarketPulse.Infrastructure.Messaging.Contracts;

/// <summary>
/// The wire format for a tick. Deliberately not <see cref="Domain.ValueObjects.PriceTick"/>:
/// a domain type and a published contract change for different reasons and at different
/// speeds, even when they happen to have the same shape today.
/// </summary>
public sealed record PriceTickMessage(string Ticker, decimal Price, DateTimeOffset TimestampUtc);
