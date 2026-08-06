using MarketPulse.Application.PriceHistory;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarketPulse.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/prices")]
public sealed class PricesController(ISender sender) : ControllerBase
{
    [HttpGet("{ticker}/candles")]
    public async Task<ActionResult<CandlesDto>> GetCandles(
        string ticker,
        [FromQuery] string interval = "1m",
        [FromQuery] string from = "",
        [FromQuery] string to = "",
        CancellationToken ct = default) =>
        Ok(await sender.Send(new GetCandlesQuery(ticker, interval, from, to), ct));
}
