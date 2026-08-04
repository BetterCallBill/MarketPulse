using MarketPulse.Application.Alerts;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarketPulse.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/alerts")]
public sealed class AlertsController(ISender sender) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AlertRuleDto>>> Get(CancellationToken ct) =>
        Ok(await sender.Send(new GetAlertRulesQuery(), ct));

    [HttpPost]
    public async Task<ActionResult<AlertRuleDto>> Create(
        [FromBody] CreateAlertRuleCommand command, CancellationToken ct)
    {
        var rule = await sender.Send(command, ct);

        // No route value, because there is no GET /alerts/{id} to point one at. Passing
        // `new { id = rule.Id }` to the collection action produced
        // `Location: /api/v1/alerts?id=<guid>` — a URL that ignores the query string and
        // returns every rule the user has. The collection is the honest answer: the created
        // rule is in it, and a Location header that lies is worse than a coarse one. A
        // single-rule endpoint would be a new item on the spec's API surface with nothing
        // asking for it; the client already has the rule in this response body.
        return CreatedAtAction(nameof(Get), rule);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await sender.Send(new DeleteAlertRuleCommand(id), ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/rearm")]
    public async Task<ActionResult<AlertRuleDto>> Rearm(Guid id, CancellationToken ct) =>
        Ok(await sender.Send(new RearmAlertRuleCommand(id), ct));
}
