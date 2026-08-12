using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Exceptions;
using MediatR;

namespace MarketPulse.Application.Notifications;

public record MarkNotificationReadCommand(Guid Id) : IRequest;

public sealed class MarkNotificationReadHandler(
    INotificationRepository repo, ICurrentUser user)
    : IRequestHandler<MarkNotificationReadCommand>
{
    public async Task Handle(MarkNotificationReadCommand request, CancellationToken ct)
    {
        var notification = await repo.GetByIdAsync(request.Id, user.UserId, ct)
            ?? throw new NotificationNotFoundException();

        notification.MarkRead();
        await repo.SaveChangesAsync(ct);
    }
}
