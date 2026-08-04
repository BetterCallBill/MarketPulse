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
        return CreatedAtAction(nameof(Get), new { id = rule.Id }, rule);
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
