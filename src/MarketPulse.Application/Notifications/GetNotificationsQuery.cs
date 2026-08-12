using FluentValidation;
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using MediatR;

namespace MarketPulse.Application.Notifications;

public record NotificationDto(
    Guid Id,
    Guid AlertRuleId,
    string Ticker,
    string Direction,
    decimal Threshold,
    decimal TriggeredPrice,
    DateTimeOffset OccurredUtc,
    bool IsRead);

public record GetNotificationsQuery(int Skip = 0, int Take = 50)
    : IRequest<IReadOnlyList<NotificationDto>>;

public sealed class GetNotificationsValidator : AbstractValidator<GetNotificationsQuery>
{
    public GetNotificationsValidator()
    {
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0)
            .WithMessage("Skip cannot be negative.").WithErrorCode("invalid-paging");

        // Capped, not clamped: silently returning 100 when 5000 was asked for is a lie the
        // caller cannot detect.
        RuleFor(x => x.Take).InclusiveBetween(1, 100)
            .WithMessage("Take must be between 1 and 100.").WithErrorCode("invalid-paging");
    }
}

public sealed class GetNotificationsHandler(INotificationRepository repo, ICurrentUser user)
    : IRequestHandler<GetNotificationsQuery, IReadOnlyList<NotificationDto>>
{
    public async Task<IReadOnlyList<NotificationDto>> Handle(
        GetNotificationsQuery request, CancellationToken ct)
    {
        var notifications = await repo.GetForUserAsync(
            user.UserId, request.Skip, request.Take, ct);

        return notifications.Select(n => n.ToDto()).ToList();
    }
}

internal static class NotificationMappings
{
    public static NotificationDto ToDto(this Notification n) => new(
        n.Id, n.AlertRuleId, n.Ticker, n.Direction.ToString(), n.Threshold,
        n.TriggeredPrice, n.OccurredUtc, n.IsRead);
}
