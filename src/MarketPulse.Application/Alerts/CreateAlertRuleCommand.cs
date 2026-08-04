using FluentValidation;
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using MarketPulse.Domain.Exceptions;
using MediatR;

namespace MarketPulse.Application.Alerts;

public record AlertRuleDto(
    Guid Id,
    string Ticker,
    string Direction,
    decimal Threshold,
    string Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? TriggeredUtc,
    decimal? TriggeredPrice);

/// <summary>
/// Direction arrives as a string rather than the enum so that a bad value is a 400 with a
/// readable message, not a model-binding failure that bypasses the validator.
/// </summary>
public record CreateAlertRuleCommand(string Ticker, string Direction, decimal Threshold)
    : IRequest<AlertRuleDto>;

public sealed class CreateAlertRuleValidator : AbstractValidator<CreateAlertRuleCommand>
{
    public CreateAlertRuleValidator(IAlertRuleRepository repo)
    {
        RuleFor(x => x.Ticker)
            .NotEmpty().WithMessage("Ticker is required.").WithErrorCode("invalid-ticker")
            .MaximumLength(8).WithMessage("Ticker must be 8 characters or fewer.")
                .WithErrorCode("invalid-ticker")
            .MustAsync(async (ticker, ct) =>
                await repo.TickerExistsAsync(ticker.Trim().ToUpperInvariant(), ct))
            .WithMessage(x => $"'{x.Ticker}' is not a known ticker.")
            .WithErrorCode("unknown-ticker");

        RuleFor(x => x.Direction)
            .Must(d => Enum.TryParse<AlertDirection>(d, ignoreCase: true, out _))
            .WithMessage("Direction must be 'Above' or 'Below'.")
            .WithErrorCode("invalid-direction");

        RuleFor(x => x.Threshold)
            .GreaterThan(0).WithMessage("An alert threshold must be greater than zero.")
            .WithErrorCode("invalid-threshold");
    }
}

public sealed class CreateAlertRuleHandler(IAlertRuleRepository repo, ICurrentUser user)
    : IRequestHandler<CreateAlertRuleCommand, AlertRuleDto>
{
    public async Task<AlertRuleDto> Handle(CreateAlertRuleCommand request, CancellationToken ct)
    {
        var ticker = request.Ticker.Trim().ToUpperInvariant();
        var direction = Enum.Parse<AlertDirection>(request.Direction, ignoreCase: true);

        // Neither of these can live on the aggregate: a standalone rule can see neither its
        // siblings nor the ticker table. See the spec's domain-model section.
        if (await repo.CountForUserAsync(user.UserId, ct) >= AlertRule.MaxPerUser)
        {
            throw new AlertRuleLimitException(AlertRule.MaxPerUser);
        }

        if (await repo.ActiveDuplicateExistsAsync(
                user.UserId, ticker, direction, request.Threshold, ct))
        {
            throw new DuplicateAlertRuleException(ticker);
        }

        var rule = AlertRule.Create(
            user.UserId, ticker, direction, request.Threshold, DateTimeOffset.UtcNow);

        await repo.AddAsync(rule, ct);
        await repo.SaveChangesAsync(ct);

        return rule.ToDto();
    }
}

internal static class AlertRuleMappings
{
    public static AlertRuleDto ToDto(this AlertRule r) => new(
        r.Id, r.Ticker, r.Direction.ToString(), r.Threshold, r.Status.ToString(),
        r.CreatedUtc, r.TriggeredUtc, r.TriggeredPrice);
}
