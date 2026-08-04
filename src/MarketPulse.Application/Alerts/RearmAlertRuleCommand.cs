using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Exceptions;
using MediatR;

namespace MarketPulse.Application.Alerts;

public record RearmAlertRuleCommand(Guid Id) : IRequest<AlertRuleDto>;

public sealed class RearmAlertRuleHandler(IAlertRuleRepository repo, ICurrentUser user)
    : IRequestHandler<RearmAlertRuleCommand, AlertRuleDto>
{
    public async Task<AlertRuleDto> Handle(RearmAlertRuleCommand request, CancellationToken ct)
    {
        var rule = await repo.GetByIdAsync(request.Id, user.UserId, ct)
            ?? throw new AlertRuleNotFoundException();

        // Throws AlertNotTriggeredException if it was never fired.
        rule.Rearm();
        await repo.SaveChangesAsync(ct);

        return rule.ToDto();
    }
}
