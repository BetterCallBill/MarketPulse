using System.Net.Http.Json;
using System.Text.Json;

namespace MarketPulse.Infrastructure.RealTime;

/// <summary>
/// One symbol, one request, one nullable price. The keyless v8 chart endpoint is the
/// stable unofficial surface (the batched v7 quote endpoint has been crumb-gated since
/// 2023 — see ADR-010). Anything that is not a positive price in a well-formed 200 is
/// null: the caller decides what a missing price means; transport failures propagate to
/// the resilience pipeline that owns them.
/// </summary>
public sealed class YahooQuoteClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<decimal?> GetPriceAsync(string yahooSymbol, CancellationToken ct)
    {
        using var response = await http.GetAsync(
            $"/v8/finance/chart/{Uri.EscapeDataString(yahooSymbol)}?interval=1d&range=1d", ct);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        ChartEnvelope? parsed;
        try
        {
            parsed = await response.Content.ReadFromJsonAsync<ChartEnvelope>(JsonOptions, ct);
        }
        catch (JsonException)
        {
            return null;
        }

        var price = parsed?.Chart?.Result?.FirstOrDefault()?.Meta?.RegularMarketPrice;
        return price is > 0 ? price : null;
    }

    private sealed record ChartEnvelope(ChartBody? Chart);
    private sealed record ChartBody(IReadOnlyList<ChartResult>? Result);
    private sealed record ChartResult(Meta? Meta);
    private sealed record Meta(decimal? RegularMarketPrice);
}
