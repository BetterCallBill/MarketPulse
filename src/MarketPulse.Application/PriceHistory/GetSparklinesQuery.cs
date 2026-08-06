using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using MediatR;
using Microsoft.Extensions.Options;

namespace MarketPulse.Application.PriceHistory;

public record SparklinesDto(IReadOnlyDictionary<string, IReadOnlyList<decimal>> Sparklines);

public record GetSparklinesQuery : IRequest<SparklinesDto>;

public sealed class GetSparklinesHandler(
    IPriceHistoryReader reader,
    IWatchlistRepository watchlists,
    ICurrentUser user,
    IOptions<HistoryOptions> options,
    TimeProvider timeProvider) : IRequestHandler<GetSparklinesQuery, SparklinesDto>
{
    public async Task<SparklinesDto> Handle(GetSparklinesQuery request, CancellationToken ct)
    {
        var watchlist = await watchlists.GetForUserAsync(user.UserId, ct);
        var tickers = watchlist?.Items.Select(i => i.Ticker).OrderBy(t => t, StringComparer.Ordinal).ToList() ?? [];

        var to = timeProvider.GetUtcNow();
        var from = to.AddMinutes(-options.Value.SparklineWindowMinutes);

        // DELIBERATE N+1 (ADR-006): one candle query per watchlist ticker. Correct and
        // fully tested — which is the point: no test in this suite can see the defect.
        // Measured and replaced by a set-based read later in this same slice; both
        // measurements live in docs/sql/.
        var sparklines = new Dictionary<string, IReadOnlyList<decimal>>();
        foreach (var ticker in tickers)
        {
            var candles = await reader.GetCandlesAsync(ticker, 60, from, to, ct);
            sparklines[ticker] = candles.Select(c => c.Close).ToList();
        }

        return new SparklinesDto(sparklines);
    }
}
