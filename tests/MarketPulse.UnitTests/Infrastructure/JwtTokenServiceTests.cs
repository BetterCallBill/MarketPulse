using System.IdentityModel.Tokens.Jwt;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.Entities;
using MarketPulse.Infrastructure.Authentication;
using Microsoft.Extensions.Options;

namespace MarketPulse.UnitTests.Infrastructure;

public class JwtTokenServiceTests
{
    private static readonly JwtOptions Options = new()
    {
        SigningKey = "test-signing-key-that-is-long-enough-32",
        Issuer = "marketpulse-tests",
        Audience = "marketpulse-tests",
        AccessTokenLifetime = TimeSpan.FromMinutes(15)
    };

    private readonly JwtTokenService _service = new(Microsoft.Extensions.Options.Options.Create(Options));

    private static readonly User TestUser =
        new(Guid.Parse("44444444-4444-4444-4444-444444444444"),
            "someone@marketpulse.local", "hash", DateTimeOffset.UnixEpoch);

    [Fact]
    public void The_access_token_carries_the_user_id_as_sub()
    {
        var token = _service.CreateAccessToken(TestUser);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal(TestUser.Id.ToString(), jwt.Claims.Single(c => c.Type == "sub").Value);
    }

    [Fact]
    public void The_access_token_carries_the_issuer_audience_and_expiry()
    {
        var token = _service.CreateAccessToken(TestUser);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("marketpulse-tests", jwt.Issuer);
        Assert.Contains("marketpulse-tests", jwt.Audiences);
        Assert.True(jwt.ValidTo > DateTime.UtcNow.AddMinutes(14));
        Assert.True(jwt.ValidTo < DateTime.UtcNow.AddMinutes(16));
    }

    [Fact]
    public void Each_refresh_token_is_unique()
    {
        var (first, _) = _service.CreateRefreshToken();
        var (second, _) = _service.CreateRefreshToken();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void The_returned_hash_matches_hashing_the_returned_token()
    {
        var (token, hash) = _service.CreateRefreshToken();

        Assert.Equal(hash, _service.HashRefreshToken(token));
    }

    [Fact]
    public void The_refresh_token_is_not_its_own_hash()
    {
        var (token, hash) = _service.CreateRefreshToken();

        Assert.NotEqual(token, hash);
    }
}
