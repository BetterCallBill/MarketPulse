using MarketPulse.Domain.Entities;
using MarketPulse.Domain.Exceptions;

namespace MarketPulse.UnitTests.Domain;

public class PortfolioTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 0, 0, 0, TimeSpan.Zero);

    private static Portfolio NewPortfolio() => Portfolio.Create(Guid.NewGuid());

    [Fact]
    public void A_first_buy_creates_the_holding_at_the_fill_price()
    {
        var portfolio = NewPortfolio();

        var tx = portfolio.RecordBuy("IVV", 10m, 60m, Now, Now);

        var holding = Assert.Single(portfolio.Holdings);
        Assert.Equal("IVV", holding.Ticker);
        Assert.Equal(10m, holding.Units);
        Assert.Equal(60m, holding.AverageCost);
        Assert.Equal(0m, holding.RealisedPnL);
        Assert.Equal(TransactionSide.Buy, tx.Side);
        Assert.Equal(portfolio.Id, tx.PortfolioId);
    }

    [Fact]
    public void A_second_buy_reaverages_the_cost()
    {
        // 10 @ 60 then 10 @ 80 → 20 units at (600 + 800) / 20 = 70.
        var portfolio = NewPortfolio();
        portfolio.RecordBuy("IVV", 10m, 60m, Now, Now);

        portfolio.RecordBuy("IVV", 10m, 80m, Now, Now);

        var holding = Assert.Single(portfolio.Holdings);
        Assert.Equal(20m, holding.Units);
        Assert.Equal(70m, holding.AverageCost);
    }

    [Fact]
    public void A_sell_realises_pnl_against_average_cost_and_leaves_the_average_unchanged()
    {
        // 20 @ avg 70, sell 5 @ 90 → realised 5 · (90 − 70) = 100; avg stays 70.
        var portfolio = NewPortfolio();
        portfolio.RecordBuy("IVV", 10m, 60m, Now, Now);
        portfolio.RecordBuy("IVV", 10m, 80m, Now, Now);

        portfolio.RecordSell("IVV", 5m, 90m, Now, Now);

        var holding = Assert.Single(portfolio.Holdings);
        Assert.Equal(15m, holding.Units);
        Assert.Equal(70m, holding.AverageCost);
        Assert.Equal(100m, holding.RealisedPnL);
    }

    [Fact]
    public void Selling_at_a_loss_realises_negative_pnl()
    {
        var portfolio = NewPortfolio();
        portfolio.RecordBuy("NDQ", 10m, 50m, Now, Now);

        portfolio.RecordSell("NDQ", 4m, 45m, Now, Now);

        Assert.Equal(-20m, Assert.Single(portfolio.Holdings).RealisedPnL);
    }

    [Fact]
    public void Selling_to_zero_keeps_the_holding_and_a_rebuy_resets_the_basis()
    {
        // The basis reset falls out of the averaging arithmetic, not a special case:
        // (0 · anything + 10 · 40) / 10 = 40.
        var portfolio = NewPortfolio();
        portfolio.RecordBuy("IVV", 10m, 60m, Now, Now);
        portfolio.RecordSell("IVV", 10m, 65m, Now, Now);

        var flat = Assert.Single(portfolio.Holdings);
        Assert.Equal(0m, flat.Units);
        Assert.Equal(50m, flat.RealisedPnL); // 10 · (65 − 60)

        portfolio.RecordBuy("IVV", 10m, 40m, Now, Now);

        var rebought = Assert.Single(portfolio.Holdings);
        Assert.Equal(10m, rebought.Units);
        Assert.Equal(40m, rebought.AverageCost);
        Assert.Equal(50m, rebought.RealisedPnL); // history survives the flat period
    }

    [Fact]
    public void Overselling_throws_and_names_both_quantities()
    {
        var portfolio = NewPortfolio();
        portfolio.RecordBuy("IVV", 5m, 60m, Now, Now);

        var ex = Assert.Throws<InsufficientHoldingsException>(
            () => portfolio.RecordSell("IVV", 6m, 60m, Now, Now));

        Assert.Contains("5", ex.Message);
        Assert.Contains("6", ex.Message);
        Assert.Equal(422, ex.StatusCode);
    }

    [Fact]
    public void Selling_a_ticker_never_bought_throws()
    {
        Assert.Throws<InsufficientHoldingsException>(
            () => NewPortfolio().RecordSell("IVV", 1m, 60m, Now, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_units_are_rejected_on_both_sides(decimal units)
    {
        var portfolio = NewPortfolio();
        Assert.Throws<InvalidTradeException>(() => portfolio.RecordBuy("IVV", units, 60m, Now, Now));
        Assert.Throws<InvalidTradeException>(() => portfolio.RecordSell("IVV", units, 60m, Now, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.01)]
    public void Non_positive_prices_are_rejected(decimal price)
    {
        Assert.Throws<InvalidTradeException>(
            () => NewPortfolio().RecordBuy("IVV", 1m, price, Now, Now));
    }

    [Fact]
    public void Fractional_units_average_exactly_in_decimal()
    {
        // 0.3 @ 100 and 0.6 @ 40 → 0.9 units at (30 + 24) / 0.9 = 60 exactly — the
        // arithmetic that would drift in double stays exact in decimal.
        var portfolio = NewPortfolio();
        portfolio.RecordBuy("VHY", 0.3m, 100m, Now, Now);
        portfolio.RecordBuy("VHY", 0.6m, 40m, Now, Now);

        Assert.Equal(60m, Assert.Single(portfolio.Holdings).AverageCost);
    }

    [Fact]
    public void Tickers_are_normalised_like_the_rest_of_the_domain()
    {
        var portfolio = NewPortfolio();
        portfolio.RecordBuy(" ivv ", 1m, 60m, Now, Now);

        Assert.Equal("IVV", Assert.Single(portfolio.Holdings).Ticker);
    }

    [Fact]
    public void A_trade_stamps_the_root_with_when_it_was_recorded()
    {
        // Load-bearing, not informational: this is what puts the portfolio row — and its
        // RowVersion — into every trade's unit of work (see the property's doc comment).
        var portfolio = NewPortfolio();
        Assert.Null(portfolio.LastTradedUtc);

        portfolio.RecordBuy("IVV", 1m, 60m, Now, Now);
        Assert.Equal(Now, portfolio.LastTradedUtc);

        var later = Now.AddMinutes(1);
        portfolio.RecordSell("IVV", 1m, 60m, later, later);
        Assert.Equal(later, portfolio.LastTradedUtc);
    }

    [Fact]
    public void A_rejected_trade_does_not_stamp_the_root()
    {
        var portfolio = NewPortfolio();

        Assert.Throws<InsufficientHoldingsException>(
            () => portfolio.RecordSell("IVV", 1m, 60m, Now, Now));

        Assert.Null(portfolio.LastTradedUtc);
    }
}
