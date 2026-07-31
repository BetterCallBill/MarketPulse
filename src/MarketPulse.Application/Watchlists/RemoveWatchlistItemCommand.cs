using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Exceptions;
using MediatR;

namespace MarketPulse.Application.Watchlists;

public record RemoveWatchlistItemCommand(string Ticker) : IRequest<WatchlistDto>;

public sealed class RemoveWatchlistItemHandler(IWatchlistRepository repo, ICurrentUser user)
    : IRequestHandler<RemoveWatchlistItemCommand, WatchlistDto>
{
    public async Task<WatchlistDto> Handle(RemoveWatchlistItemCommand request, CancellationToken ct)
    {
        var watchlist = await repo.GetForUserAsync(user.UserId, ct)
            ?? throw new TickerNotOnWatchlistException(request.Ticker);

        watchlist.RemoveItem(request.Ticker);
        await repo.SaveChangesAsync(ct);

        return watchlist.ToDto();
    }
}
