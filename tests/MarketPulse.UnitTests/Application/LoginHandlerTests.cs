using System.Diagnostics.Metrics;
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Authentication;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.Entities;
using MarketPulse.Domain.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MarketPulse.UnitTests.Application;

public class LoginHandlerTests
{
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IRefreshTokenRepository _refreshTokens = Substitute.For<IRefreshTokenRepository>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly ITokenService _tokens = Substitute.For<ITokenService>();

    private static readonly AuthOptions Auth = new()
    {
        MaxFailedAttempts = 5,
        LockoutDuration = TimeSpan.FromMinutes(15)
    };

    private static readonly JwtOptions Jwt = new()
    {
        SigningKey = "test-signing-key-that-is-long-enough-32",
        Issuer = "t",
        Audience = "t"
    };

    private LoginHandler CreateHandler(ILogger<LoginHandler>? logger = null) => new(
        _users, _refreshTokens, _hasher, _tokens,
        Options.Create(Jwt), Options.Create(Auth), logger ?? NullLogger<LoginHandler>.Instance);

    private static User AUser() =>
        User.Register("someone@marketpulse.local", "stored-hash");

    public LoginHandlerTests()
    {
        _tokens.CreateRefreshToken().Returns(("refresh-token", "refresh-hash"));
        _tokens.CreateAccessToken(Arg.Any<User>()).Returns("access-token");
    }

    [Fact]
    public async Task An_unknown_email_is_rejected_as_invalid_credentials()
    {
        _users.GetByEmailAsync("nobody@marketpulse.local", Arg.Any<CancellationToken>())
            .Returns((User?)null);

        await Assert.ThrowsAsync<InvalidCredentialsException>(() =>
            CreateHandler().Handle(
                new LoginCommand("nobody@marketpulse.local", "whatever12345"),
                CancellationToken.None));
    }

    [Fact]
    public async Task An_unknown_email_still_runs_a_hash_verification()
    {
        // Timing equalisation: returning early without hashing makes "unknown email"
        // measurably faster than "wrong password", which is an enumeration oracle.
        _users.GetByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);

        await Assert.ThrowsAsync<InvalidCredentialsException>(() =>
            CreateHandler().Handle(
                new LoginCommand("nobody@marketpulse.local", "whatever12345"),
                CancellationToken.None));

        _hasher.Received().Verify(Arg.Any<string>(), "whatever12345");
    }

    [Fact]
    public async Task A_wrong_password_increments_the_failure_count()
    {
        var user = AUser();
        _users.GetByEmailAsync(user.Email, Arg.Any<CancellationToken>()).Returns(user);
        _hasher.Verify("stored-hash", "wrong-password").Returns(false);

        await Assert.ThrowsAsync<InvalidCredentialsException>(() =>
            CreateHandler().Handle(
                new LoginCommand(user.Email, "wrong-password"), CancellationToken.None));

        Assert.Equal(1, user.FailedLoginCount);
        await _users.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_locked_account_is_rejected_before_the_password_is_checked()
    {
        var user = AUser();
        for (var i = 0; i < 5; i++)
        {
            user.RecordFailedLogin(DateTimeOffset.UtcNow, 5, TimeSpan.FromMinutes(15));
        }

        _users.GetByEmailAsync(user.Email, Arg.Any<CancellationToken>()).Returns(user);

        await Assert.ThrowsAsync<AccountLockedException>(() =>
            CreateHandler().Handle(
                new LoginCommand(user.Email, "correct-password"), CancellationToken.None));

        _hasher.DidNotReceive().Verify("stored-hash", "correct-password");
    }

    [Fact]
    public async Task A_correct_password_returns_a_session_and_resets_the_counter()
    {
        var user = AUser();
        user.RecordFailedLogin(DateTimeOffset.UtcNow, 5, TimeSpan.FromMinutes(15));

        _users.GetByEmailAsync(user.Email, Arg.Any<CancellationToken>()).Returns(user);
        _hasher.Verify("stored-hash", "right-password").Returns(true);

        var result = await CreateHandler().Handle(
            new LoginCommand(user.Email, "right-password"), CancellationToken.None);

        Assert.Equal("access-token", result.AccessToken);
        Assert.Equal("refresh-token", result.RefreshToken);
        Assert.Equal(user.Id, result.UserId);
        Assert.Equal(0, user.FailedLoginCount);
    }

    [Fact]
    public async Task A_wrong_password_logs_a_masked_email_and_increments_login_failures()
    {
        var user = AUser();
        _users.GetByEmailAsync(user.Email, Arg.Any<CancellationToken>()).Returns(user);
        _hasher.Verify("stored-hash", "wrong-password").Returns(false);
        var logger = new FakeLogger<LoginHandler>();

        var delta = await MeasureCounterDeltaAsync(
            global::MarketPulse.Application.Telemetry.Telemetry.AuthLoginFailures,
            () => Assert.ThrowsAsync<InvalidCredentialsException>(() =>
                CreateHandler(logger).Handle(
                    new LoginCommand(user.Email, "wrong-password"), CancellationToken.None)));

        Assert.Equal(1, delta);

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Contains("s***@marketpulse.local", record.Message);
        Assert.DoesNotContain(user.Email, record.Message);
    }

    [Fact]
    public async Task A_locked_account_increments_the_lockout_counter()
    {
        var user = AUser();
        for (var i = 0; i < 5; i++)
        {
            user.RecordFailedLogin(DateTimeOffset.UtcNow, 5, TimeSpan.FromMinutes(15));
        }

        _users.GetByEmailAsync(user.Email, Arg.Any<CancellationToken>()).Returns(user);

        var delta = await MeasureCounterDeltaAsync(
            global::MarketPulse.Application.Telemetry.Telemetry.AuthLockouts,
            () => Assert.ThrowsAsync<AccountLockedException>(() =>
                CreateHandler().Handle(
                    new LoginCommand(user.Email, "correct-password"), CancellationToken.None)));

        Assert.Equal(1, delta);
    }

    private static async Task<long> MeasureCounterDeltaAsync(Counter<long> counter, Func<Task> act)
    {
        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument == counter)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => observed += value);
        listener.Start();

        var before = observed;
        await act();
        return observed - before;
    }
}
