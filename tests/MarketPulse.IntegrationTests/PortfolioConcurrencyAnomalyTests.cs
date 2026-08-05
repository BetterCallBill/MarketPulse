using MarketPulse.Domain.Entities;
using MarketPulse.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.IntegrationTests;

/// <summary>
/// The pair that makes ADR-004's isolation discussion executable. Under read-committed —
/// SQL Server's default, and what every request here runs at — two interleaved
/// read-validate-write sequences on the same holding are a classic lost-update: both
/// validate against the same snapshot, both commit, and the second silently erases the
/// first's effect. Test (a) reproduces exactly that by stripping the one defence the
/// system has; test (b) shows the defence — the RowVersion token — turning the same
/// interleaving into a 409 for the loser. Serializable isolation would also prevent it,
/// at the cost of deadlock-retry plumbing on every write; the token localises the cost
/// to the one aggregate that races. See ADR-004.
///
/// "The one defence" is the RowVersion check on the Portfolios row's UPDATE. A buy/sell
/// only mutates a Holding, which lives in its own table via the owned-collection mapping —
/// nothing about that write would otherwise touch the Portfolios row at all, so RowVersion
/// would never enter a trade's WHERE clause. Portfolio.Version is what closes that gap: it
/// is bumped by every RecordBuy/RecordSell, forcing the root row into the same unit of work
/// as the trade, which is what puts RowVersion's check into every trade's UPDATE. Without
/// that counter this pair would look different — test (b) would fail to throw, because nothing
/// would ever validate the token on a holding-only change. See Portfolio.Version's doc comment.
///
/// Test (a)'s own doc comment explains why "erases the first's effect" is proved by replaying
/// the loser's trade against reality rather than by an oversold, negative balance.
/// </summary>
[Collection(nameof(SqlServerCollection))]
public class PortfolioConcurrencyAnomalyTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Establishing the actual behaviour (per the task brief's caveat process) surfaced a
    /// second, distinct fact beyond the root-token question: <see cref="Holding.Units"/> is
    /// persisted as the aggregate's current absolute balance, not a compounding SQL delta —
    /// EF's generated UPDATE does <c>SET Units = @currentValue</c>, computed once in memory
    /// from whatever snapshot the context loaded, never <c>SET Units = Units - @amount</c>.
    /// Two racers who both validate against the *same* original snapshot therefore each
    /// compute a value that is, on its own, always within [0, snapshot] — whichever commits
    /// last simply overwrites with its own in-range number. That makes literal negative
    /// units mathematically unreachable through this race, for any choice of amounts: the
    /// still-real anomaly is not "the balance goes negative" but "a trade that correct,
    /// non-racing validation would reject is instead accepted and silently overwrites the
    /// other's effect, and the persisted balance gives no sign that anything was lost."
    /// This test proves that version of the anomaly directly, by replaying the loser's exact
    /// trade against the state the winner actually left behind.
    /// </summary>
    [Fact]
    public async Task Without_the_token_two_racing_sells_both_succeed_though_one_should_have_been_rejected()
    {
        var portfolioId = await SeedPortfolioAsync(units: 10m);

        // Both contexts load the same state: 10 units held.
        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        var copy1 = await first.Portfolios.SingleAsync(p => p.Id == portfolioId);
        var copy2 = await second.Portfolios.SingleAsync(p => p.Id == portfolioId);

        // Both validate against the same stale snapshot (10 units) and pass: 7 <= 10 and
        // 6 <= 10. This is the read-validate-write interleaving. Note the two trades are
        // mutually exclusive under correct serialisation: 7 + 6 = 13 exceeds the 10 ever
        // held, so whichever trade validates second (against real, post-first state) must
        // fail. The race is precisely what lets both validate as if they went first.
        copy1.RecordSell("IVV", 7m, 60m, Now, Now);
        copy2.RecordSell("IVV", 6m, 60m, Now, Now);

        await first.SaveChangesAsync(); // DB now holds 3 units (10 - 7).

        // The proof: replay copy2's exact trade against the state first's commit actually
        // left behind (3 units, freshly loaded — no bypass, no race, just honest
        // read-then-validate). It is rejected. This is what should have stopped the second
        // sell; the race let it through instead.
        await using (var honestCheck = fixture.CreateContext())
        {
            var honestCopy = await honestCheck.Portfolios.SingleAsync(p => p.Id == portfolioId);
            Assert.Throws<InsufficientHoldingsException>(
                () => honestCopy.RecordSell("IVV", 6m, 60m, Now, Now));
        }

        // The bypass: hand the second context the winner's current token, so its UPDATE's
        // WHERE clause matches — this is what every write would behave like if the token
        // did not exist. Read-committed alone does not save you: each statement saw only
        // committed data, and the anomaly happened anyway.
        var currentVersion = await first.Portfolios
            .Where(p => p.Id == portfolioId)
            .Select(p => p.RowVersion)
            .SingleAsync();

        var rootEntry = second.Entry(copy2); // token lives on the aggregate root
        rootEntry.Property(nameof(Portfolio.RowVersion)).OriginalValue = currentVersion;

        await second.SaveChangesAsync(); // commits — the lost update, and the rejected trade lands anyway

        await using var check = fixture.CreateContext();
        var portfolio = await check.Portfolios.SingleAsync(p => p.Id == portfolioId);
        var holding = portfolio.Holdings.Single();

        // The anomaly, asserted: the trade proven above to be invalid (6 units, with only 3
        // actually available) is persisted as if it were fine, and the first sell's effect
        // is gone without a trace — the balance (4 = 10 - 6) looks like a single ordinary
        // sell, not like two trades totalling 13 units against a holding of 10.
        Assert.Equal(4m, holding.Units);

        // Both sells incremented Version from the same loaded value (1 -> 2), so the second
        // write recomputes the identical number rather than building on the first commit's
        // result. A correctly serialised pair of trades would land on 3 (seed buy = 1, then
        // +1 per sell); the bypass leaves it at 2 — one increment is silently lost, same as
        // the holding balance. The counter that exists to guard against exactly this is
        // itself a casualty of the bypass: not the anomaly this test is about (Units is),
        // but worth pinning down that the lost update is total, not partial.
        Assert.Equal(2, portfolio.Version);
    }

    [Fact]
    public async Task With_the_token_the_second_sell_loses_with_a_concurrency_exception()
    {
        var portfolioId = await SeedPortfolioAsync(units: 10m);

        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        var copy1 = await first.Portfolios.SingleAsync(p => p.Id == portfolioId);
        var copy2 = await second.Portfolios.SingleAsync(p => p.Id == portfolioId);

        copy1.RecordSell("IVV", 8m, 60m, Now, Now);
        copy2.RecordSell("IVV", 8m, 60m, Now, Now);

        await first.SaveChangesAsync();

        // The real path: stale token, UPDATE matches no row, EF throws — the API's
        // middleware turns this into the 409 the client retries on.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        await using var check = fixture.CreateContext();
        Assert.Equal(2m, (await check.Portfolios.SingleAsync(p => p.Id == portfolioId))
            .Holdings.Single().Units);
    }

    private async Task<Guid> SeedPortfolioAsync(decimal units)
    {
        // Direct insert via the domain factory, matching PortfolioPersistenceTests.NewUserAsync
        // rather than the HTTP registration flow — this is a persistence-layer test, and a
        // real Users row is all a Portfolio's FK needs.
        Guid userId;
        await using (var userDb = fixture.CreateContext())
        {
            var user = User.Register($"anomaly-{Guid.NewGuid():N}@marketpulse.local", "hash");
            userDb.Users.Add(user);
            await userDb.SaveChangesAsync();
            userId = user.Id;
        }

        await using var db = fixture.CreateContext();
        var portfolio = Portfolio.Create(userId);
        var tx = portfolio.RecordBuy("IVV", units, 60m, Now, Now);
        db.Portfolios.Add(portfolio);
        db.Transactions.Add(tx);
        await db.SaveChangesAsync();
        return portfolio.Id;
    }
}
