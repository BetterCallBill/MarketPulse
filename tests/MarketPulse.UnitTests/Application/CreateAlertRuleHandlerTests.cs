using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Alerts;
using MarketPulse.Domain.Entities;
using MarketPulse.Domain.Exceptions;
using NSubstitute;

namespace MarketPulse.UnitTests.Application;

public class CreateAlertRuleHandlerTests
{
    private readonly IAlertRuleRepository _repo = Substitute.For<IAlertRuleRepository>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly Guid _userId = Guid.NewGuid();

    public CreateAlertRuleHandlerTests() => _user.UserId.Returns(_userId);

    private CreateAlertRuleHandler Handler() => new(_repo, _user);

    private static CreateAlertRuleCommand Command(string direction = "Above") =>
        new("IVV", direction, 50m);

    [Fact]
    public async Task A_rule_is_created_and_saved()
    {
        var result = await Handler().Handle(Command(), CancellationToken.None);

        Assert.Equal("IVV", result.Ticker);
        Assert.Equal("Above", result.Direction);
        Assert.Equal("Active", result.Status);
        await _repo.Received(1).AddAsync(Arg.Any<AlertRule>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_twenty_first_rule_is_rejected()
    {
        _repo.CountForUserAsync(_userId, Arg.Any<CancellationToken>())
             .Returns(AlertRule.MaxPerUser);

        await Assert.ThrowsAsync<AlertRuleLimitException>(
            () => Handler().Handle(Command(), CancellationToken.None));

        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_identical_active_rule_is_rejected()
    {
        _repo.ActiveDuplicateExistsAsync(
                _userId, "IVV", AlertDirection.Above, 50m, Arg.Any<CancellationToken>())
             .Returns(true);

        await Assert.ThrowsAsync<DuplicateAlertRuleException>(
            () => Handler().Handle(Command(), CancellationToken.None));
    }

    [Fact]
    public async Task The_limit_is_checked_before_the_duplicate_check()
    {
        // Both fail. The user has a real ceiling problem and a cosmetic one; tell them
        // about the ceiling, because deleting the duplicate would not help them.
        _repo.CountForUserAsync(_userId, Arg.Any<CancellationToken>())
             .Returns(AlertRule.MaxPerUser);
        _repo.ActiveDuplicateExistsAsync(
                _userId, "IVV", AlertDirection.Above, 50m, Arg.Any<CancellationToken>())
             .Returns(true);

        await Assert.ThrowsAsync<AlertRuleLimitException>(
            () => Handler().Handle(Command(), CancellationToken.None));
    }
}
