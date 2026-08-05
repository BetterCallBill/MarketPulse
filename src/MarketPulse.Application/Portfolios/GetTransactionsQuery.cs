using FluentValidation;
using MarketPulse.Application.Abstractions;
using MediatR;

namespace MarketPulse.Application.Portfolios;

public record TransactionDto(
    Guid Id, string Ticker, string Side, decimal Units, decimal Price,
    DateTimeOffset OccurredUtc, DateTimeOffset RecordedUtc);

public record GetTransactionsQuery(int Skip = 0, int Take = 50)
    : IRequest<IReadOnlyList<TransactionDto>>;

public sealed class GetTransactionsValidator : AbstractValidator<GetTransactionsQuery>
{
    public GetTransactionsValidator()
    {
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0)
            .WithMessage("Skip cannot be negative.").WithErrorCode("invalid-paging");

        RuleFor(x => x.Take).InclusiveBetween(1, 100)
            .WithMessage("Take must be between 1 and 100.").WithErrorCode("invalid-paging");
    }
}

public sealed class GetTransactionsHandler(IPortfolioRepository repo, ICurrentUser user)
    : IRequestHandler<GetTransactionsQuery, IReadOnlyList<TransactionDto>>
{
    public async Task<IReadOnlyList<TransactionDto>> Handle(
        GetTransactionsQuery request, CancellationToken ct)
    {
        var portfolio = await repo.GetForUserAsync(user.UserId, ct);
        if (portfolio is null)
        {
            return [];
        }

        var transactions = await repo.GetTransactionsAsync(portfolio.Id, request.Skip, request.Take, ct);

        return transactions
            .Select(t => new TransactionDto(
                t.Id, t.Ticker, t.Side.ToString(), t.Units, t.Price, t.OccurredUtc, t.RecordedUtc))
            .ToList();
    }
}
