using System.Net;
using System.Net.Http.Json;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class PortfolioApiTests(SqlServerFixture fixture)
{
    private sealed record HoldingResponse(string Ticker, decimal Units, decimal AverageCost, decimal RealisedPnL);
    private sealed record PortfolioResponse(List<HoldingResponse> Holdings, decimal TotalRealisedPnL);
    private sealed record TransactionResponse(Guid Id, string Ticker, string Side, decimal Units, decimal Price);

    private static object NewTrade(
        string ticker = "IVV", string side = "Buy", decimal units = 10m, decimal price = 60m,
        DateTimeOffset? occurredUtc = null) =>
        new { Ticker = ticker, Side = side, Units = units, Price = price, OccurredUtc = occurredUtc };

    [Fact]
    public async Task A_buy_then_partial_sell_produce_correct_average_cost_and_realised_pnl()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        var firstBuy = await client.PostAsJsonAsync(
            "/api/v1/portfolio/transactions", NewTrade(units: 10m, price: 60m));
        Assert.Equal(HttpStatusCode.Created, firstBuy.StatusCode);

        var secondBuy = await client.PostAsJsonAsync(
            "/api/v1/portfolio/transactions", NewTrade(units: 10m, price: 80m));
        Assert.Equal(HttpStatusCode.Created, secondBuy.StatusCode);

        var sell = await client.PostAsJsonAsync(
            "/api/v1/portfolio/transactions", NewTrade(side: "Sell", units: 5m, price: 90m));
        Assert.Equal(HttpStatusCode.Created, sell.StatusCode);

        var portfolio = await client.GetFromJsonAsync<PortfolioResponse>("/api/v1/portfolio");

        var holding = Assert.Single(portfolio!.Holdings);
        Assert.Equal("IVV", holding.Ticker);
        Assert.Equal(15m, holding.Units);
        Assert.Equal(70m, holding.AverageCost);
        Assert.Equal(100m, holding.RealisedPnL);
        Assert.Equal(100m, portfolio.TotalRealisedPnL);
    }

    [Fact]
    public async Task An_empty_portfolio_is_200_with_no_holdings()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        var response = await client.GetAsync("/api/v1/portfolio");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var portfolio = await response.Content.ReadFromJsonAsync<PortfolioResponse>();
        Assert.Empty(portfolio!.Holdings);
        Assert.Equal(0m, portfolio.TotalRealisedPnL);
    }

    [Fact]
    public async Task Overselling_is_422_with_the_named_code()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        await client.PostAsJsonAsync("/api/v1/portfolio/transactions", NewTrade(units: 5m));
        var oversell = await client.PostAsJsonAsync(
            "/api/v1/portfolio/transactions", NewTrade(side: "Sell", units: 6m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, oversell.StatusCode);

        var problem = await oversell.Content.ReadFromJsonAsync<ProblemResponse>();
        Assert.Equal("insufficient-holdings", problem!.Title);
    }

    [Fact]
    public async Task An_unknown_ticker_is_400()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        var response = await client.PostAsJsonAsync(
            "/api/v1/portfolio/transactions", NewTrade(ticker: "ZZZZ"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();
        Assert.Equal("unknown-ticker", problem!.Title);
    }

    [Fact]
    public async Task A_bad_side_is_400()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        var response = await client.PostAsJsonAsync(
            "/api/v1/portfolio/transactions", NewTrade(side: "Hold"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();
        Assert.Equal("invalid-side", problem!.Title);
    }

    [Fact]
    public async Task Transactions_page_newest_first()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        var oldest = DateTimeOffset.UtcNow.AddDays(-2);
        var middle = DateTimeOffset.UtcNow.AddDays(-1);
        var newest = DateTimeOffset.UtcNow;

        // Distinct unit counts so each trade is identifiable in the returned pages.
        await client.PostAsJsonAsync(
            "/api/v1/portfolio/transactions", NewTrade(units: 1m, occurredUtc: oldest));
        await client.PostAsJsonAsync(
            "/api/v1/portfolio/transactions", NewTrade(units: 2m, occurredUtc: middle));
        await client.PostAsJsonAsync(
            "/api/v1/portfolio/transactions", NewTrade(units: 3m, occurredUtc: newest));

        var firstPage = await client.GetFromJsonAsync<List<TransactionResponse>>(
            "/api/v1/portfolio/transactions?take=2");
        Assert.Equal([3m, 2m], firstPage!.Select(t => t.Units));

        var secondPage = await client.GetFromJsonAsync<List<TransactionResponse>>(
            "/api/v1/portfolio/transactions?take=2&skip=2");
        Assert.Equal([1m], secondPage!.Select(t => t.Units));
    }

    [Fact]
    public async Task Transactions_for_a_fresh_user_are_empty()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        var response = await client.GetAsync("/api/v1/portfolio/transactions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var transactions = await response.Content.ReadFromJsonAsync<List<TransactionResponse>>();
        Assert.Empty(transactions!);
    }

    private sealed record ProblemResponse(string Title);
}
