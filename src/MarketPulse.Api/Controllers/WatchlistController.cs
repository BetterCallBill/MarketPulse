using MarketPulse.Application.Watchlists;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace MarketPulse.Api.Controllers;

[ApiController]
[Route("api/v1/watchlist")]
public sealed class WatchlistController(ISender sender) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<WatchlistDto>> Get(CancellationToken ct) =>
        Ok(await sender.Send(new GetWatchlistQuery(), ct));

    [HttpPost("items")]
    public async Task<ActionResult<WatchlistDto>> AddItem(
        [FromBody] AddWatchlistItemCommand command, CancellationToken ct) =>
        Ok(await sender.Send(command, ct));

    [HttpDelete("items/{ticker}")]
    public async Task<ActionResult<WatchlistDto>> RemoveItem(string ticker, CancellationToken ct) =>
        Ok(await sender.Send(new RemoveWatchlistItemCommand(ticker), ct));
}
