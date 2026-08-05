using System.Net;
using System.Net.Http.Json;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class IdempotencyTests(SqlServerFixture fixture)
{
    private sealed record ProblemResponse(string Title);
    private sealed record RuleResponse(
        Guid Id, string Ticker, string Direction, decimal Threshold, string Status);

    private static object NewRule(
        string ticker = "IVV", string direction = "Above", decimal threshold = 50m) =>
        new { Ticker = ticker, Direction = direction, Threshold = threshold };

    private static HttpRequestMessage BuyRequest(string key, decimal units = 1m, decimal price = 60m)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/portfolio/transactions")
        {
            Content = JsonContent.Create(new { Ticker = "IVV", Side = "Buy", Units = units, Price = price })
        };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static HttpRequestMessage SellRequest(string key, decimal units = 1m, decimal price = 60m)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/portfolio/transactions")
        {
            Content = JsonContent.Create(new { Ticker = "IVV", Side = "Sell", Units = units, Price = price })
        };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static HttpRequestMessage AlertRequest(string key) =>
        new(HttpMethod.Post, "/api/v1/alerts")
        {
            Content = JsonContent.Create(NewRule()),
            Headers = { { "Idempotency-Key", key } }
        };

    [Fact]
    public async Task Replaying_a_key_returns_the_stored_response_and_records_one_transaction()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);
        var key = Guid.NewGuid().ToString();

        var first = await client.SendAsync(BuyRequest(key));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync();

        var second = await client.SendAsync(BuyRequest(key));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var secondBody = await second.Content.ReadAsStringAsync();

        Assert.Equal(firstBody, secondBody);

        var transactions = await client.GetFromJsonAsync<List<object>>("/api/v1/portfolio/transactions");
        Assert.Single(transactions!);

        var portfolio = await client.GetFromJsonAsync<PortfolioResponse>("/api/v1/portfolio");
        var holding = Assert.Single(portfolio!.Holdings);
        Assert.Equal(1m, holding.Units);
    }

    [Fact]
    public async Task The_same_key_with_a_different_body_is_422()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);
        var key = Guid.NewGuid().ToString();

        var first = await client.SendAsync(BuyRequest(key, units: 1m));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.SendAsync(BuyRequest(key, units: 2m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);

        var problem = await second.Content.ReadFromJsonAsync<ProblemResponse>();
        Assert.Equal("idempotency-key-reuse", problem!.Title);
    }

    [Fact]
    public async Task Keys_are_scoped_per_user()
    {
        await using var factory = TestFactory.Create(fixture);
        var alice = await AuthenticatedClient.RegisterAsync(factory);
        var bob = await AuthenticatedClient.RegisterAsync(factory);
        const string sharedKey = "shared-key";

        var aliceResponse = await alice.SendAsync(BuyRequest(sharedKey));
        var bobResponse = await bob.SendAsync(BuyRequest(sharedKey));

        Assert.Equal(HttpStatusCode.Created, aliceResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, bobResponse.StatusCode);

        var aliceTx = await alice.GetFromJsonAsync<List<object>>("/api/v1/portfolio/transactions");
        var bobTx = await bob.GetFromJsonAsync<List<object>>("/api/v1/portfolio/transactions");

        Assert.Single(aliceTx!);
        Assert.Single(bobTx!);
    }

    [Fact]
    public async Task A_failed_request_stores_nothing_so_the_key_is_reusable()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);
        var key = Guid.NewGuid().ToString();

        // No holdings yet: this sell must fail, and fail without claiming the key.
        var failedSell = await client.SendAsync(SellRequest(key, units: 1m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, failedSell.StatusCode);
        var problem = await failedSell.Content.ReadFromJsonAsync<ProblemResponse>();
        Assert.Equal("insufficient-holdings", problem!.Title);

        // Give ourselves holdings under a different key.
        var buy = await client.SendAsync(BuyRequest(Guid.NewGuid().ToString(), units: 5m));
        Assert.Equal(HttpStatusCode.Created, buy.StatusCode);

        // Retry the exact same key with the exact same (now-satisfiable) sell body.
        var retry = await client.SendAsync(SellRequest(key, units: 1m));
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    [Fact]
    public async Task Two_concurrent_requests_with_one_fresh_key_execute_exactly_once()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);
        var key = Guid.NewGuid().ToString();

        var first = client.SendAsync(BuyRequest(key));
        var second = client.SendAsync(BuyRequest(key));
        var responses = await Task.WhenAll(first, second);

        foreach (var response in responses)
        {
            Assert.True(
                response.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict,
                $"Unexpected status {response.StatusCode}");
        }

        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.Created);

        var transactions = await client.GetFromJsonAsync<List<object>>("/api/v1/portfolio/transactions");
        Assert.Single(transactions!);
    }

    [Fact]
    public async Task A_request_without_the_header_executes_normally_every_time()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        var first = await client.PostAsJsonAsync(
            "/api/v1/portfolio/transactions",
            new { Ticker = "IVV", Side = "Buy", Units = 1m, Price = 60m });
        var second = await client.PostAsJsonAsync(
            "/api/v1/portfolio/transactions",
            new { Ticker = "IVV", Side = "Buy", Units = 1m, Price = 60m });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var transactions = await client.GetFromJsonAsync<List<object>>("/api/v1/portfolio/transactions");
        Assert.Equal(2, transactions!.Count);
    }

    [Fact]
    public async Task Post_alerts_honours_the_same_header()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);
        var key = Guid.NewGuid().ToString();

        var first = await client.SendAsync(AlertRequest(key));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync();

        var second = await client.SendAsync(AlertRequest(key));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var secondBody = await second.Content.ReadAsStringAsync();

        Assert.Equal(firstBody, secondBody);

        var rules = await client.GetFromJsonAsync<List<RuleResponse>>("/api/v1/alerts");
        Assert.Single(rules!);
    }

    private sealed record HoldingResponse(string Ticker, decimal Units, decimal AverageCost, decimal RealisedPnL);
    private sealed record PortfolioResponse(List<HoldingResponse> Holdings, decimal TotalRealisedPnL);
}
