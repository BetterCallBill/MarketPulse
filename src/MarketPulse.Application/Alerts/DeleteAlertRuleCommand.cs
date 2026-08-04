using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Exceptions;
using MediatR;

namespace MarketPulse.Application.Alerts;

public record DeleteAlertRuleCommand(Guid Id) : IRequest;

public sealed class DeleteAlertRuleHandler(IAlertRuleRepository repo, ICurrentUser user)
    : IRequestHandler<DeleteAlertRuleCommand>
{
    public async Task Handle(DeleteAlertRuleCommand request, CancellationToken ct)
    {
        var rule = await repo.GetByIdAsync(request.Id, user.UserId, ct)
            ?? throw new AlertRuleNotFoundException();

        repo.Remove(rule);
        await repo.SaveChangesAsync(ct);
    }
}
