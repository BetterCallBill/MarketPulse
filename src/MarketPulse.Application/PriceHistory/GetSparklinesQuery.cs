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

        // Set-based since ADR-006: one query for every ticker, then empty lists filled in for
        // tickers with no ticks in the window so the response shape stays total over the watchlist.
        var withData = await reader.GetSparklinesAsync(tickers, from, to, ct);
        var sparklines = tickers.ToDictionary(
            t => t,
            t => withData.GetValueOrDefault(t, []),
            StringComparer.Ordinal);

        return new SparklinesDto(sparklines);
    }
}
