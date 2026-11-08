using System.Text.Json;
using FluentValidation;
using Incidents.Application.Commands;
using Incidents.Application.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Incidents.Api.Controllers;

// Slack Events API endpoint. Slack expects a 2xx within 3 seconds, so we only verify,
// validate and enqueue (ReportIncidentCommand). All slow work happens in the Functions.
[ApiController]
[Route("api/slack")]
public class SlackController(IMediator mediator, IConfiguration config, ILogger<SlackController> log) : ControllerBase
{
    [HttpPost("events")]
    public async Task<IActionResult> Events(CancellationToken ct)
    {
        var body = await new StreamReader(Request.Body).ReadToEndAsync(ct);
        if (!WebhookSignatures.VerifySlack(config["Slack:SigningSecret"] ?? "",
                Request.Headers["X-Slack-Request-Timestamp"], body, Request.Headers["X-Slack-Signature"], DateTimeOffset.UtcNow))
            return Unauthorized();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();

        if (type == "url_verification") // one-time handshake when configuring the Slack app
            return Ok(new { challenge = root.GetProperty("challenge").GetString() });

        if (type != "event_callback") return Ok();
        var ev = root.GetProperty("event");
        if (ev.GetProperty("type").GetString() != "app_mention" || ev.TryGetProperty("bot_id", out _))
            return Ok(); // only human mentions of our bot; ignoring bots prevents loops

        var ts = ev.GetProperty("ts").GetString()!;
        var threadTs = ev.TryGetProperty("thread_ts", out var t) ? t.GetString()! : ts;
        try
        {
            await mediator.Send(new ReportIncidentCommand(
                root.GetProperty("event_id").GetString()!,
                ev.GetProperty("text").GetString() ?? "",
                ev.GetProperty("user").GetString() ?? "",
                ev.GetProperty("channel").GetString() ?? "",
                threadTs,
                DateTimeOffset.FromUnixTimeMilliseconds((long)(double.Parse(ts, System.Globalization.CultureInfo.InvariantCulture) * 1000))), ct);
        }
        catch (ValidationException e)
        {
            // Still 200: a non-2xx makes Slack retry the same invalid event.
            log.LogWarning("Rejected Slack incident: {Errors}", e.Message);
        }
        return Ok();
    }
}
