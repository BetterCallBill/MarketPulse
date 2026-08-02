using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Authentication;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.Entities;
using MarketPulse.Domain.Exceptions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MarketPulse.UnitTests.Application;

public class RefreshSessionHandlerTests
{
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IRefreshTokenRepository _refreshTokens = Substitute.For<IRefreshTokenRepository>();
    private readonly ITokenService _tokens = Substitute.For<ITokenService>();

    private static readonly JwtOptions Jwt = new()
    {
        SigningKey = "test-signing-key-that-is-long-enough-32",
        Issuer = "t",
        Audience = "t",
        RefreshTokenLifetime = TimeSpan.FromDays(14)
    };

    private readonly User _user = User.Register("someone@marketpulse.local", "hash");

    private RefreshSessionHandler CreateHandler() =>
        new(_users, _refreshTokens, _tokens, Options.Create(Jwt));

    public RefreshSessionHandlerTests()
    {
        _tokens.HashRefreshToken("presented").Returns("presented-hash");
        _tokens.CreateRefreshToken().Returns(("new-token", "new-hash"));
        _tokens.CreateAccessToken(Arg.Any<User>()).Returns("access-token");
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
    }

    [Fact]
    public async Task An_unknown_token_is_rejected()
    {
        _refreshTokens.GetByHashAsync("presented-hash", Arg.Any<CancellationToken>())
            .Returns((RefreshToken?)null);

        await Assert.ThrowsAsync<SessionRevokedException>(() =>
            CreateHandler().Handle(new RefreshSessionCommand("presented"), CancellationToken.None));
    }

    [Fact]
    public async Task An_expired_token_is_rejected()
    {
        var expired = RefreshToken.Issue(
            _user.Id, "presented-hash", DateTimeOffset.UtcNow.AddDays(-20), TimeSpan.FromDays(14));
        _refreshTokens.GetByHashAsync("presented-hash", Arg.Any<CancellationToken>()).Returns(expired);

        await Assert.ThrowsAsync<SessionRevokedException>(() =>
            CreateHandler().Handle(new RefreshSessionCommand("presented"), CancellationToken.None));
    }

    [Fact]
    public async Task Replaying_a_revoked_token_revokes_the_whole_family()
    {
        var revoked = RefreshToken.Issue(
            _user.Id, "presented-hash", DateTimeOffset.UtcNow, TimeSpan.FromDays(14));
        revoked.Revoke(DateTimeOffset.UtcNow);
        _refreshTokens.GetByHashAsync("presented-hash", Arg.Any<CancellationToken>()).Returns(revoked);

        await Assert.ThrowsAsync<SessionRevokedException>(() =>
            CreateHandler().Handle(new RefreshSessionCommand("presented"), CancellationToken.None));

        await _refreshTokens.Received().RevokeAllForUserAsync(
            _user.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_valid_token_rotates_and_records_its_successor()
    {
        var live = RefreshToken.Issue(
            _user.Id, "presented-hash", DateTimeOffset.UtcNow, TimeSpan.FromDays(14));
        _refreshTokens.GetByHashAsync("presented-hash", Arg.Any<CancellationToken>()).Returns(live);

        var result = await CreateHandler().Handle(
            new RefreshSessionCommand("presented"), CancellationToken.None);

        Assert.Equal("new-token", result.RefreshToken);
        Assert.Equal("access-token", result.AccessToken);
        Assert.False(live.IsActive(DateTimeOffset.UtcNow));
        Assert.NotNull(live.ReplacedByTokenId);
    }
}
