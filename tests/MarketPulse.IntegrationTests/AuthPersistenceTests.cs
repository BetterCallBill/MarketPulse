using MarketPulse.Domain.Entities;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class AuthPersistenceTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task The_seeded_dev_user_has_a_usable_password_hash()
    {
        await using var db = fixture.CreateContext();

        var dev = await db.Users.SingleAsync(u => u.Id == SeedData.DevUserId);

        Assert.Equal(SeedData.DevUserEmail, dev.Email);
        Assert.False(string.IsNullOrWhiteSpace(dev.PasswordHash));

        var hasher = new Infrastructure.Authentication.PasswordHasherAdapter();
        Assert.True(hasher.Verify(dev.PasswordHash, SeedData.DevUserPassword));
    }

    [Fact]
    public async Task A_user_round_trips_through_the_repository()
    {
        var email = $"user-{Guid.NewGuid():N}@marketpulse.local";

        await using (var write = fixture.CreateContext())
        {
            var repo = new UserRepository(write);
            await repo.AddAsync(User.Register(email, "hashed"), CancellationToken.None);
            await repo.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = fixture.CreateContext();
        var found = await new UserRepository(read)
            .GetByEmailAsync(email.ToUpperInvariant(), CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(email, found!.Email);
    }

    [Fact]
    public async Task Revoking_a_family_revokes_every_live_token_for_that_user_only()
    {
        var mine = User.Register($"mine-{Guid.NewGuid():N}@marketpulse.local", "h");
        var theirs = User.Register($"theirs-{Guid.NewGuid():N}@marketpulse.local", "h");
        var now = DateTimeOffset.UtcNow;

        await using (var write = fixture.CreateContext())
        {
            write.Users.AddRange(mine, theirs);
            write.RefreshTokens.AddRange(
                RefreshToken.Issue(mine.Id, $"a-{Guid.NewGuid():N}", now, TimeSpan.FromDays(14)),
                RefreshToken.Issue(mine.Id, $"b-{Guid.NewGuid():N}", now, TimeSpan.FromDays(14)),
                RefreshToken.Issue(theirs.Id, $"c-{Guid.NewGuid():N}", now, TimeSpan.FromDays(14)));
            await write.SaveChangesAsync();
        }

        await using (var act = fixture.CreateContext())
        {
            var repo = new RefreshTokenRepository(act);
            await repo.RevokeAllForUserAsync(mine.Id, now, CancellationToken.None);
            await repo.SaveChangesAsync(CancellationToken.None);
        }

        await using var assert = fixture.CreateContext();
        Assert.Empty(assert.RefreshTokens.Where(t => t.UserId == mine.Id && t.RevokedUtc == null));
        Assert.Single(assert.RefreshTokens.Where(t => t.UserId == theirs.Id && t.RevokedUtc == null));
    }
}
