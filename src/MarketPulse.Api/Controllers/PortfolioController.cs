using MarketPulse.Application.Portfolios;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarketPulse.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/portfolio")]
public sealed class PortfolioController(ISender sender) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PortfolioDto>> Get(CancellationToken ct) =>
        Ok(await sender.Send(new GetPortfolioQuery(), ct));

    [HttpPost("transactions")]
    public async Task<ActionResult<PortfolioDto>> Record(
        [FromBody] RecordTransactionCommand command, CancellationToken ct)
    {
        var portfolio = await sender.Send(command, ct);

        // 201 pointing at the portfolio the trade changed — the transaction itself has no
        // GET-by-id endpoint, and inventing one for a Location header repeats 4a's mistake.
        return CreatedAtAction(nameof(Get), portfolio);
    }

    [HttpGet("transactions")]
    public async Task<ActionResult<IReadOnlyList<TransactionDto>>> Transactions(
        [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default) =>
        Ok(await sender.Send(new GetTransactionsQuery(skip, take), ct));
}
