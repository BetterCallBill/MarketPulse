using System.Net;
using System.Net.Http.Json;
using MarketPulse.Application.PriceHistory;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class CandleApiTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = TestFactory.Create(fixture);
        _client = await AuthenticatedClient.RegisterAsync(_factory);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static string Url(string ticker, string interval = "1m",
        string from = "2026-08-06T00:00:00Z", string to = "2026-08-06T01:00:00Z") =>
        $"/api/v1/prices/{ticker}/candles?interval={interval}&from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}";

    [Fact]
    public async Task Known_ticker_with_no_data_returns_200_and_empty_candles()
    {
        var response = await _client.GetAsync(Url("IVV", from: "2020-01-01T00:00:00Z", to: "2020-01-01T01:00:00Z"));

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<CandlesDto>();
        Assert.NotNull(body);
        Assert.Equal("IVV", body!.Ticker);
        Assert.Empty(body.Candles);
    }

    [Fact]
    public async Task Unknown_ticker_returns_404_unknown_ticker()
    {
        var response = await _client.GetAsync(Url("ZZZZ"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("unknown-ticker", body!["title"].ToString());
    }

    [Fact]
    public async Task Unlisted_interval_returns_400_invalid_interval()
    {
        var response = await _client.GetAsync(Url("IVV", interval: "42s"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("invalid-interval", body!["title"].ToString());
    }

    [Fact]
    public async Task Inverted_or_unparseable_range_returns_400_invalid_range()
    {
        var inverted = await _client.GetAsync(
            Url("IVV", from: "2026-08-06T02:00:00Z", to: "2026-08-06T01:00:00Z"));
        var garbage = await _client.GetAsync(Url("IVV", from: "not-a-date"));

        Assert.Equal(HttpStatusCode.BadRequest, inverted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
        var body = await inverted.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("invalid-range", body!["title"].ToString());
    }

    [Fact]
    public async Task A_range_exceeding_the_bucket_cap_returns_400_range_too_large()
    {
        // 1000-bucket cap at 1m: 30 days is 43,200 buckets
        var response = await _client.GetAsync(
            Url("IVV", from: "2026-07-01T00:00:00Z", to: "2026-08-01T00:00:00Z"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("range-too-large", body!["title"].ToString());
    }

    [Fact]
    public async Task Anonymous_requests_are_rejected()
    {
        var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync(Url("IVV"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
