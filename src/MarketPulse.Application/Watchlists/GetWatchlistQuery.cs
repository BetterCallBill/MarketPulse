using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using MediatR;

namespace MarketPulse.Application.Watchlists;

public record WatchlistItemDto(string Ticker, DateTimeOffset AddedUtc);

public record WatchlistDto(Guid Id, IReadOnlyList<WatchlistItemDto> Items);

public record GetWatchlistQuery : IRequest<WatchlistDto>;

public sealed class GetWatchlistHandler(IWatchlistRepository repo, ICurrentUser user)
    : IRequestHandler<GetWatchlistQuery, WatchlistDto>
{
    public async Task<WatchlistDto> Handle(GetWatchlistQuery request, CancellationToken ct)
    {
        var watchlist = await repo.GetForUserAsync(user.UserId, ct);

        if (watchlist is null)
        {
            watchlist = Watchlist.Create(user.UserId);
            await repo.AddAsync(watchlist, ct);
            await repo.SaveChangesAsync(ct);
        }

        return watchlist.ToDto();
    }
}

internal static class WatchlistMappings
{
    public static WatchlistDto ToDto(this Watchlist w) => new(
        w.Id,
        w.Items.OrderBy(i => i.Ticker)
               .Select(i => new WatchlistItemDto(i.Ticker, i.AddedUtc))
               .ToList());
}
