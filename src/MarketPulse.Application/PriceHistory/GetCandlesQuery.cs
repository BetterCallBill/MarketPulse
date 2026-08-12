using System.Globalization;
using FluentValidation;
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.Exceptions;
using MediatR;
using Microsoft.Extensions.Options;

namespace MarketPulse.Application.PriceHistory;

public record CandleDto(DateTimeOffset T, decimal O, decimal H, decimal L, decimal C);

public record CandlesDto(string Ticker, string Interval, IReadOnlyList<CandleDto> Candles);

/// <summary>
/// From/To arrive as raw strings so the validator owns every failure mode with a slugged
/// 400 — model binding never gets the chance to reject with an unslugged one.
/// </summary>
public record GetCandlesQuery(string Ticker, string Interval, string From, string To)
    : IRequest<CandlesDto>;

public static class CandleIntervals
{
    public static readonly IReadOnlyDictionary<string, int> Seconds =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["1m"] = 60, ["5m"] = 300, ["1h"] = 3600, ["1d"] = 86_400
        };
}

public sealed class GetCandlesValidator : AbstractValidator<GetCandlesQuery>
{
    public GetCandlesValidator(IOptions<HistoryOptions> options)
    {
        RuleFor(x => x.Ticker)
            .NotEmpty().WithErrorCode("invalid-ticker")
            .MaximumLength(8).WithErrorCode("invalid-ticker");

        RuleFor(x => x.Interval)
            .Must(CandleIntervals.Seconds.ContainsKey)
            .WithErrorCode("invalid-interval")
            .WithMessage("Interval must be one of: 1m, 5m, 1h, 1d.");

        RuleFor(x => x)
            .Must(q => TryParseRange(q, out _, out _))
            .WithErrorCode("invalid-range")
            .WithMessage("From and To must be valid timestamps with From earlier than To.");

        RuleFor(x => x)
            .Must(q => !TryParseRange(q, out var from, out var to)
                       || !CandleIntervals.Seconds.TryGetValue(q.Interval, out var seconds)
                       || (to - from).TotalSeconds / seconds <= options.Value.MaxCandleBuckets)
            .WithErrorCode("range-too-large")
            .WithMessage($"Range must not exceed {options.Value.MaxCandleBuckets} buckets.");
    }

    internal static bool TryParseRange(GetCandlesQuery q, out DateTimeOffset from, out DateTimeOffset to)
    {
        to = default;
        return DateTimeOffset.TryParse(q.From, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out from)
            && DateTimeOffset.TryParse(q.To, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out to)
            && from < to;
    }
}

public sealed class GetCandlesHandler(IPriceHistoryReader reader, IWatchlistRepository watchlists)
    : IRequestHandler<GetCandlesQuery, CandlesDto>
{
    public async Task<CandlesDto> Handle(GetCandlesQuery request, CancellationToken ct)
    {
        var ticker = request.Ticker.ToUpperInvariant();

        if (!await watchlists.TickerExistsAsync(ticker, ct))
        {
            throw new TickerNotFoundException(ticker);
        }

        // The validator has already proven this parses; parse again rather than smuggle
        // state between pipeline stages.
        GetCandlesValidator.TryParseRange(request, out var from, out var to);

        var candles = await reader.GetCandlesAsync(
            ticker, CandleIntervals.Seconds[request.Interval], from, to, ct);

        return new CandlesDto(
            ticker,
            request.Interval,
            candles.Select(c => new CandleDto(c.BucketStartUtc, c.Open, c.High, c.Low, c.Close)).ToList());
    }
}
