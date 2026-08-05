using MarketPulse.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class PortfolioPersistenceTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_portfolio_round_trips_with_holdings_transactions_and_precision_intact()
    {
        var userId = await NewUserAsync();
        var portfolio = Portfolio.Create(userId);
        var tx1 = portfolio.RecordBuy("IVV", 0.123456m, 60.1234m, Now, Now);
        var tx2 = portfolio.RecordBuy("IVV", 0.2m, 61m, Now, Now);

        await using (var db = fixture.CreateContext())
        {
            db.Portfolios.Add(portfolio);
            db.Transactions.AddRange(tx1, tx2);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var loaded = await db.Portfolios
                .SingleAsync(p => p.UserId == userId);
            var holding = Assert.Single(loaded.Holdings);

            // Units survive at (18,6); money at (18,4).
            Assert.Equal(0.323456m, holding.Units);
            Assert.NotEmpty(loaded.RowVersion);

            Assert.Equal(2, await db.Transactions.CountAsync(t => t.PortfolioId == loaded.Id));
        }
    }

    [Fact]
    public async Task A_stale_portfolio_write_throws_the_concurrency_exception()
    {
        var userId = await NewUserAsync();
        var portfolio = Portfolio.Create(userId);
        portfolio.RecordBuy("IVV", 10m, 60m, Now, Now);

        await using (var db = fixture.CreateContext())
        {
            db.Portfolios.Add(portfolio);
            await db.SaveChangesAsync();
        }

        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        var copy1 = await first.Portfolios.SingleAsync(p => p.UserId == userId);
        var copy2 = await second.Portfolios.SingleAsync(p => p.UserId == userId);

        copy1.RecordSell("IVV", 1m, 60m, Now, Now);
        await first.SaveChangesAsync();

        copy2.RecordSell("IVV", 1m, 60m, Now, Now);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    private async Task<Guid> NewUserAsync()
    {
        // Direct insert via the domain factory, matching AlertPersistenceTests.NewUserAsync
        // rather than the HTTP registration flow — this is a persistence-layer test, and a
        // real Users row is all a Portfolio's FK needs.
        await using var db = fixture.CreateContext();
        var user = User.Register($"persist-{Guid.NewGuid():N}@marketpulse.local", "hash");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }
}
