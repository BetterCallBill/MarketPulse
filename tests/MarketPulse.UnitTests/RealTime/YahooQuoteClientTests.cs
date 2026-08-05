using System.Net;
using System.Text;
using MarketPulse.Infrastructure.RealTime;

namespace MarketPulse.UnitTests.RealTime;

/// <summary>Scripted HttpMessageHandler: each call pops the next response (or throws).</summary>
internal sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] script)
    : HttpMessageHandler
{
    private int _calls;

    public int Calls => _calls;
    public List<Uri?> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri);
        var step = script[Math.Min(_calls, script.Length - 1)];
        _calls++;
        return Task.FromResult(step(request));
    }
}

public class YahooQuoteClientTests
{
    /// <summary>
    /// The v8 chart shape as Yahoo actually returns it (captured 2026-08, trimmed to the
    /// fields the client reads plus realistic noise it must ignore).
    /// </summary>
    private const string IvvChartJson = """
    {
      "chart": {
        "result": [
          {
            "meta": {
              "currency": "AUD",
              "symbol": "IVV.AX",
              "exchangeName": "ASX",
              "instrumentType": "ETF",
              "regularMarketPrice": 62.41,
              "regularMarketTime": 1754355600,
              "previousClose": 62.10
            },
            "timestamp": [1754355600],
            "indicators": { "quote": [ { "close": [62.41] } ] }
          }
        ],
        "error": null
      }
    }
    """;

    private static YahooQuoteClient Client(ScriptedHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://stub.local") });

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Parses_the_regular_market_price_from_a_real_chart_response()
    {
        var handler = new ScriptedHandler(_ => Json(IvvChartJson));

        var price = await Client(handler).GetPriceAsync("IVV.AX", CancellationToken.None);

        Assert.Equal(62.41m, price);
        Assert.Equal(
            "/v8/finance/chart/IVV.AX?interval=1d&range=1d",
            handler.Requests[0]!.PathAndQuery);
    }

    [Theory]
    [InlineData("""{ "chart": { "result": null, "error": { "code": "Not Found" } } }""")]
    [InlineData("""{ "chart": { "result": [], "error": null } }""")]
    [InlineData("""{ "chart": { "result": [ { "meta": { "symbol": "IVV.AX" } } ], "error": null } }""")]
    [InlineData("this is not json")]
    public async Task An_unparseable_or_priceless_body_yields_null(string body)
    {
        var handler = new ScriptedHandler(_ => Json(body));

        Assert.Null(await Client(handler).GetPriceAsync("IVV.AX", CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1.5)]
    public async Task A_non_positive_price_yields_null(decimal price)
    {
        var body = IvvChartJson.Replace("62.41", price.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        var handler = new ScriptedHandler(_ => Json(body));

        Assert.Null(await Client(handler).GetPriceAsync("IVV.AX", CancellationToken.None));
    }

    [Fact]
    public async Task A_non_success_status_yields_null_rather_than_throwing()
    {
        var handler = new ScriptedHandler(_ => Json("{}", HttpStatusCode.TooManyRequests));

        Assert.Null(await Client(handler).GetPriceAsync("IVV.AX", CancellationToken.None));
    }
}

public class YahooSymbolsTests
{
    [Fact]
    public void Maps_both_directions_and_rejects_foreign_symbols()
    {
        Assert.Equal("IVV.AX", YahooSymbols.ToYahoo("IVV"));
        Assert.Equal("IVV", YahooSymbols.ToAsx("IVV.AX"));
        Assert.Null(YahooSymbols.ToAsx("AAPL"));
        Assert.Null(YahooSymbols.ToAsx("IVV.NZ"));
    }
}
