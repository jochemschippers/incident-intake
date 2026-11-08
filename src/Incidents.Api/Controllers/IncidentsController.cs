using Incidents.Application.Domain;
using Incidents.Application.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Incidents.Api.Controllers;

// Query side of CQRS. Protected by an API key (see ApiKeyFilter); webhooks have their own HMAC auth.
[ApiController]
[Route("api")]
[ServiceFilter(typeof(ApiKeyFilter))]
public class IncidentsController(IMediator mediator) : ControllerBase
{
    [HttpGet("incidents/{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct) =>
        await mediator.Send(new GetIncidentQuery(id), ct) is { } i ? Ok(i) : NotFound();

    /// <param name="period">yyyy-MM, defaults to current month</param>
    [HttpGet("incidents")]
    public async Task<IActionResult> List([FromQuery] string? period, [FromQuery] Severity? severity, CancellationToken ct) =>
        Ok(await mediator.Send(new ListIncidentsQuery(period ?? CurrentPeriod, severity), ct));

    /// <summary>Count, resolved count and MTTR (minutes) per severity for a month.</summary>
    [HttpGet("analytics/mttr")]
    public async Task<IActionResult> Mttr([FromQuery] string? period, CancellationToken ct) =>
        Ok(await mediator.Send(new GetStatsQuery(period ?? CurrentPeriod), ct));

    private static string CurrentPeriod => IncidentFact.PeriodOf(DateTimeOffset.UtcNow);
}
