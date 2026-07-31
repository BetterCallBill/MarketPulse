using FluentValidation;
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using MediatR;

namespace MarketPulse.Application.Watchlists;

public record AddWatchlistItemCommand(string Ticker) : IRequest<WatchlistDto>;

public sealed class AddWatchlistItemValidator : AbstractValidator<AddWatchlistItemCommand>
{
    public AddWatchlistItemValidator(IWatchlistRepository repo)
    {
        RuleFor(x => x.Ticker)
            .NotEmpty().WithMessage("Ticker is required.").WithErrorCode("invalid-ticker")
            .MaximumLength(8).WithMessage("Ticker must be 8 characters or fewer.").WithErrorCode("invalid-ticker")
            .MustAsync(async (ticker, ct) =>
                await repo.TickerExistsAsync(ticker.Trim().ToUpperInvariant(), ct))
            .WithMessage(x => $"'{x.Ticker}' is not a known ticker.")
            .WithErrorCode("unknown-ticker");
    }
}

public sealed class AddWatchlistItemHandler(IWatchlistRepository repo, ICurrentUser user)
    : IRequestHandler<AddWatchlistItemCommand, WatchlistDto>
{
    public async Task<WatchlistDto> Handle(AddWatchlistItemCommand request, CancellationToken ct)
    {
        var watchlist = await repo.GetForUserAsync(user.UserId, ct);

        if (watchlist is null)
        {
            watchlist = Watchlist.Create(user.UserId);
            await repo.AddAsync(watchlist, ct);
        }

        watchlist.AddItem(request.Ticker);
        await repo.SaveChangesAsync(ct);

        return watchlist.ToDto();
    }
}
