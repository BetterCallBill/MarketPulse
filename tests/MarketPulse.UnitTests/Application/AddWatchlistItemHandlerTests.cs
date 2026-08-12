using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Watchlists;
using MarketPulse.Domain.Entities;
using MarketPulse.Domain.Exceptions;
using NSubstitute;

namespace MarketPulse.UnitTests.Application;

public class AddWatchlistItemHandlerTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static (AddWatchlistItemHandler Handler, IWatchlistRepository Repo) Build(Watchlist? existing)
    {
        var repo = Substitute.For<IWatchlistRepository>();
        repo.GetForUserAsync(UserId, Arg.Any<CancellationToken>()).Returns(existing);

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);

        return (new AddWatchlistItemHandler(repo, user), repo);
    }

    [Fact]
    public async Task Adds_the_ticker_and_saves()
    {
        var watchlist = Watchlist.Create(UserId);
        var (handler, repo) = Build(watchlist);

        var result = await handler.Handle(new AddWatchlistItemCommand("IVV"), CancellationToken.None);

        Assert.Contains(result.Items, i => i.Ticker == "IVV");
        await repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Creates_a_watchlist_when_the_user_has_none()
    {
        var (handler, repo) = Build(existing: null);

        await handler.Handle(new AddWatchlistItemCommand("IVV"), CancellationToken.None);

        await repo.Received(1).AddAsync(Arg.Any<Watchlist>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Propagates_the_duplicate_invariant()
    {
        var watchlist = Watchlist.Create(UserId);
        watchlist.AddItem("IVV");
        var (handler, _) = Build(watchlist);

        await Assert.ThrowsAsync<DuplicateTickerException>(
            () => handler.Handle(new AddWatchlistItemCommand("IVV"), CancellationToken.None));
    }
}
