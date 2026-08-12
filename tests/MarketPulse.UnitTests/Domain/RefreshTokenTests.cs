using MarketPulse.Domain.Entities;

namespace MarketPulse.UnitTests.Domain;

public class RefreshTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Fortnight = TimeSpan.FromDays(14);

    private static RefreshToken Issue() =>
        RefreshToken.Issue(Guid.NewGuid(), "hash", Now, Fortnight);

    [Fact]
    public void A_freshly_issued_token_is_active()
    {
        var token = Issue();

        Assert.True(token.IsActive(Now));
        Assert.Equal(Now.Add(Fortnight), token.ExpiresUtc);
        Assert.Null(token.RevokedUtc);
        Assert.Null(token.ReplacedByTokenId);
    }

    [Fact]
    public void An_expired_token_is_not_active()
    {
        var token = Issue();

        Assert.False(token.IsActive(Now.Add(Fortnight).AddSeconds(1)));
    }

    [Fact]
    public void A_revoked_token_is_not_active()
    {
        var token = Issue();

        token.Revoke(Now);

        Assert.False(token.IsActive(Now));
        Assert.Equal(Now, token.RevokedUtc);
    }

    [Fact]
    public void Replacing_a_token_revokes_it_and_records_the_successor()
    {
        var token = Issue();
        var replacementId = Guid.NewGuid();

        token.ReplaceWith(replacementId, Now);

        Assert.False(token.IsActive(Now));
        Assert.Equal(replacementId, token.ReplacedByTokenId);
        Assert.Equal(Now, token.RevokedUtc);
    }

    [Fact]
    public void Revoking_twice_keeps_the_first_revocation_time()
    {
        var token = Issue();
        token.Revoke(Now);

        token.Revoke(Now.AddHours(1));

        Assert.Equal(Now, token.RevokedUtc);
    }
}
