using FluentValidation;
using Incidents.Application.Abstractions;
using Incidents.Application.Domain;
using MediatR;

namespace Incidents.Application.Commands;

// ---------- API side: write requests are validated + queued, never written to the DB synchronously ----------

public record ReportIncidentCommand(string EventId, string Text, string User, string Channel, string ThreadTs, DateTimeOffset At) : IRequest;

public class ReportIncidentValidator : AbstractValidator<ReportIncidentCommand>
{
    public ReportIncidentValidator()
    {
        RuleFor(c => c.EventId).NotEmpty();
        RuleFor(c => c.Channel).NotEmpty();
        RuleFor(c => c.ThreadTs).NotEmpty();
        RuleFor(c => SeverityParser.Parse(c.Text).Title)
            .NotEmpty().WithMessage("Incident needs a description, e.g. '@bot sev2 checkout is down'.")
            .MaximumLength(250)
            .OverridePropertyName("Text");
    }
}

public class ReportIncidentHandler(IQueuePublisher queue) : IRequestHandler<ReportIncidentCommand>
{
    public Task Handle(ReportIncidentCommand c, CancellationToken ct) =>
        queue.SendAsync(Queues.IncidentIntake,
            new IncidentReported(c.EventId, c.Text, c.User, c.Channel, c.ThreadTs, c.At), ct);
}

public record ForwardGitHubEventCommand(GitHubIssueEvent Event) : IRequest;

public class ForwardGitHubEventHandler(IQueuePublisher queue) : IRequestHandler<ForwardGitHubEventCommand>
{
    public Task Handle(ForwardGitHubEventCommand c, CancellationToken ct) =>
        queue.SendAsync(Queues.GitHubEvents, c.Event, ct);
}

// ---------- Worker side: executed by Azure Functions from the queues ----------

public record PersistIncidentCommand(IncidentReported Message) : IRequest;

public class PersistIncidentHandler(IIncidentRepository repo, IQueuePublisher queue) : IRequestHandler<PersistIncidentCommand>
{
    public async Task Handle(PersistIncidentCommand c, CancellationToken ct)
    {
        var m = c.Message;
        var (severity, title) = SeverityParser.Parse(m.Text);
        var incident = new Incident
        {
            Id = m.IncidentId,
            Title = title,
            Severity = severity,
            Status = IncidentStatus.Open,
            ReportedBy = m.User,
            SlackChannel = m.Channel,
            SlackThreadTs = m.ThreadTs,
            ReportedAt = m.At,
        };

        // false = duplicate (Slack retry / queue redelivery) -> already handled.
        if (await repo.TryCreateAsync(incident, ct))
            await queue.SendAsync(Queues.GitHubCreate, new CreateGitHubIssue(incident.Id), ct);
    }
}

public record OpenGitHubIssueCommand(string IncidentId) : IRequest;

public class OpenGitHubIssueHandler(IIncidentRepository repo, IGitHubClient github, IIssueLookup lookup, ISlackClient slack)
    : IRequestHandler<OpenGitHubIssueCommand>
{
    public async Task Handle(OpenGitHubIssueCommand c, CancellationToken ct)
    {
        var incident = await repo.GetAsync(c.IncidentId, ct)
            ?? throw new InvalidOperationException($"Incident {c.IncidentId} not found"); // retry -> poison queue
        if (incident.GitHubIssueNumber is not null) return; // idempotent

        var body =
            $"**Severity:** {incident.Severity}\n" +
            $"**Reported by:** Slack user `{incident.ReportedBy}`\n" +
            $"**Reported at:** {incident.ReportedAt:u}\n" +
            $"**Incident id:** `{incident.Id}`\n\n" +
            "Comments on this issue are mirrored to the Slack thread. Close the issue to resolve the incident.";

        var number = await github.CreateIssueAsync(
            $"[{incident.Severity}] {incident.Title}", body,
            ["incident", incident.Severity.ToString().ToLowerInvariant()], ct);

        await lookup.SaveAsync(number, incident.Id, ct); // lookup first: GitHub events can arrive right away
        await repo.SetGitHubIssueAsync(incident.Id, number, ct);
        await slack.PostAsync(incident.SlackChannel, incident.SlackThreadTs,
            $":memo: Logged as *{incident.Severity}* and tracked in GitHub issue #{number}.", ct);
    }
}

public record ApplyGitHubEventCommand(GitHubIssueEvent Event) : IRequest;

public class ApplyGitHubEventHandler(IIssueLookup lookup, IIncidentRepository repo, ISlackClient slack)
    : IRequestHandler<ApplyGitHubEventCommand>
{
    public async Task Handle(ApplyGitHubEventCommand c, CancellationToken ct)
    {
        var e = c.Event;
        var id = await lookup.FindAsync(e.IssueNumber, ct);
        if (id is null) return; // not an incident issue
        var incident = await repo.GetAsync(id, ct);
        if (incident is null) return;

        switch (e.Action)
        {
            case GitHubIssueEvent.Comment:
                await slack.PostAsync(incident.SlackChannel, incident.SlackThreadTs,
                    $":speech_balloon: *{e.CommentAuthor}* on GitHub #{e.IssueNumber}:\n>{e.CommentBody}", ct);
                break;
            case GitHubIssueEvent.Closed when incident.Status == IncidentStatus.Open:
                incident.Resolve(e.At);
                await repo.ResolveAsync(incident.Id, e.At, ct);
                await slack.PostAsync(incident.SlackChannel, incident.SlackThreadTs,
                    $":white_check_mark: Resolved (GitHub #{e.IssueNumber} closed). Time to resolve: {incident.MinutesToResolve} min.", ct);
                break;
        }
    }
}

public record ProjectIncidentCommand(Incident Incident) : IRequest;

public class ProjectIncidentHandler(IAnalyticsStore analytics) : IRequestHandler<ProjectIncidentCommand>
{
    public Task Handle(ProjectIncidentCommand c, CancellationToken ct) =>
        analytics.UpsertAsync(IncidentFact.From(c.Incident), ct);
}
