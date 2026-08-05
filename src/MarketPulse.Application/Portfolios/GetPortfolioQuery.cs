using MarketPulse.Application.Abstractions;
using MediatR;

namespace MarketPulse.Application.Portfolios;

public record GetPortfolioQuery : IRequest<PortfolioDto>;

public sealed class GetPortfolioHandler(IPortfolioRepository repo, ICurrentUser user)
    : IRequestHandler<GetPortfolioQuery, PortfolioDto>
{
    public async Task<PortfolioDto> Handle(GetPortfolioQuery request, CancellationToken ct) =>
        // A user who never traded gets an empty portfolio, not a 404 — implicit creation
        // means absence is indistinguishable from emptiness, on purpose.
        PortfolioMapper.ToDto(await repo.GetForUserAsync(user.UserId, ct));
}
