using System.Text.Json;
using Incidents.Application.Abstractions;
using Incidents.Application.Commands;
using Incidents.Application.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Incidents.Api.Controllers;

// GitHub repository webhook (events: "Issues" + "Issue comments"), secured with the webhook secret (HMAC).
[ApiController]
[Route("api/github")]
public class GitHubController(IMediator mediator, IConfiguration config) : ControllerBase
{
    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        var body = await new StreamReader(Request.Body).ReadToEndAsync(ct);
        if (!WebhookSignatures.VerifyGitHub(config["GitHub:WebhookSecret"] ?? "", body, Request.Headers["X-Hub-Signature-256"]))
            return Unauthorized();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var kind = Request.Headers["X-GitHub-Event"].ToString();
        var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;

        GitHubIssueEvent? e = (kind, action) switch
        {
            ("issue_comment", "created") => new GitHubIssueEvent(
                root.GetProperty("issue").GetProperty("number").GetInt32(),
                GitHubIssueEvent.Comment,
                root.GetProperty("comment").GetProperty("body").GetString(),
                root.GetProperty("comment").GetProperty("user").GetProperty("login").GetString(),
                root.GetProperty("comment").GetProperty("created_at").GetDateTimeOffset()),
            ("issues", "closed") => new GitHubIssueEvent(
                root.GetProperty("issue").GetProperty("number").GetInt32(),
                GitHubIssueEvent.Closed, null, null,
                root.GetProperty("issue").GetProperty("closed_at").GetDateTimeOffset()),
            _ => null, // ping, opened, labeled, ... -> not interesting
        };

        if (e is not null) await mediator.Send(new ForwardGitHubEventCommand(e), ct);
        return Ok();
    }
}
