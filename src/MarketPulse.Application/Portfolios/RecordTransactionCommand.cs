using FluentValidation;
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using MediatR;

namespace MarketPulse.Application.Portfolios;

public record HoldingDto(string Ticker, decimal Units, decimal AverageCost, decimal RealisedPnL);

public record PortfolioDto(IReadOnlyList<HoldingDto> Holdings, decimal TotalRealisedPnL);

/// <summary>
/// Side arrives as a string for the same reason alert Direction does: a bad value should
/// be a readable 400, not a model-binding failure. OccurredUtc is optional — a trade
/// recorded after the fact keeps its real timestamp; an omitted one is "now".
/// </summary>
public record RecordTransactionCommand(
    string Ticker, string Side, decimal Units, decimal Price, DateTimeOffset? OccurredUtc)
    : IRequest<PortfolioDto>;

public sealed class RecordTransactionValidator : AbstractValidator<RecordTransactionCommand>
{
    public RecordTransactionValidator(IPortfolioRepository repo)
    {
        RuleFor(x => x.Ticker)
            .NotEmpty().WithMessage("Ticker is required.").WithErrorCode("invalid-ticker")
            .MaximumLength(8).WithMessage("Ticker must be 8 characters or fewer.")
                .WithErrorCode("invalid-ticker")
            .MustAsync(async (ticker, ct) =>
                await repo.TickerExistsAsync(ticker.Trim().ToUpperInvariant(), ct))
            .WithMessage(x => $"'{x.Ticker}' is not a known ticker.")
            .WithErrorCode("unknown-ticker");

        RuleFor(x => x.Side)
            .Must(s => Enum.TryParse<TransactionSide>(s, ignoreCase: true, out _))
            .WithMessage("Side must be 'Buy' or 'Sell'.")
            .WithErrorCode("invalid-side");

        RuleFor(x => x.Units)
            .GreaterThan(0).WithMessage("Units must be greater than zero.")
            .WithErrorCode("invalid-units");

        RuleFor(x => x.Price)
            .GreaterThan(0).WithMessage("Price must be greater than zero.")
            .WithErrorCode("invalid-price");
    }
}

public sealed class RecordTransactionHandler(IPortfolioRepository repo, ICurrentUser user)
    : IRequestHandler<RecordTransactionCommand, PortfolioDto>
{
    public async Task<PortfolioDto> Handle(RecordTransactionCommand request, CancellationToken ct)
    {
        var side = Enum.Parse<TransactionSide>(request.Side, ignoreCase: true);
        var occurred = request.OccurredUtc ?? DateTimeOffset.UtcNow;
        var recorded = DateTimeOffset.UtcNow;

        var portfolio = await repo.GetForUserAsync(user.UserId, ct);
        if (portfolio is null)
        {
            // Implicit creation: "no portfolio yet" is not a state the API exposes.
            portfolio = Portfolio.Create(user.UserId);
            await repo.AddAsync(portfolio, ct);
        }

        var transaction = side == TransactionSide.Buy
            ? portfolio.RecordBuy(request.Ticker, request.Units, request.Price, occurred, recorded)
            : portfolio.RecordSell(request.Ticker, request.Units, request.Price, occurred, recorded);

        // One unit of work: the holding's new state and the trade that caused it commit
        // together — the spec's "cannot lose a transaction" is this line pair.
        await repo.AddTransactionAsync(transaction, ct);
        await repo.SaveChangesAsync(ct);

        return PortfolioMapper.ToDto(portfolio);
    }
}

internal static class PortfolioMapper
{
    public static PortfolioDto ToDto(Domain.Entities.Portfolio? portfolio)
    {
        var holdings = portfolio?.Holdings
            .OrderBy(h => h.Ticker)
            .Select(h => new HoldingDto(h.Ticker, h.Units, h.AverageCost, h.RealisedPnL))
            .ToList() ?? [];

        return new PortfolioDto(holdings, holdings.Sum(h => h.RealisedPnL));
    }
}
