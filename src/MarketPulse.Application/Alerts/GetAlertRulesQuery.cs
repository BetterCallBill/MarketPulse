using MarketPulse.Application.Abstractions;
using MediatR;

namespace MarketPulse.Application.Alerts;

public record GetAlertRulesQuery : IRequest<IReadOnlyList<AlertRuleDto>>;

public sealed class GetAlertRulesHandler(IAlertRuleRepository repo, ICurrentUser user)
    : IRequestHandler<GetAlertRulesQuery, IReadOnlyList<AlertRuleDto>>
{
    public async Task<IReadOnlyList<AlertRuleDto>> Handle(
        GetAlertRulesQuery request, CancellationToken ct)
    {
        var rules = await repo.GetForUserAsync(user.UserId, ct);
        return rules.Select(r => r.ToDto()).ToList();
    }
}
