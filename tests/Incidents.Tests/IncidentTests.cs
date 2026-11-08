using FluentValidation;
using Incidents.Application;
using Incidents.Application.Abstractions;
using Incidents.Application.Commands;
using Incidents.Application.Domain;
using Incidents.Application.Queries;
using Incidents.Application.Security;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Severity = Incidents.Application.Domain.Severity;

namespace Incidents.Tests;

public class SeverityParserTests
{
    [Theory]
    [InlineData("<@U0BOT> sev1 Payments API returns 500", Severity.Sev1, "Payments API returns 500")]
    [InlineData("<@U0BOT> checkout slow SEV 2", Severity.Sev2, "checkout slow")]
    [InlineData("<@U0BOT> login broken", Severity.Sev3, "login broken")] // default
    [InlineData("<@U0BOT> sev9 weird", Severity.Sev3, "sev9 weird")]      // out of range -> not a severity
    public void Parses_severity_and_title(string text, Severity sev, string title) =>
        Assert.Equal((sev, title), SeverityParser.Parse(text));
}

public class WebhookSignatureTests
{
    private const string Secret = "s3cret";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void Slack_valid_signature_passes()
    {
        var sig = "v0=" + WebhookSignatures.Sign(Secret, "v0:1800000000:{\"a\":1}");
        Assert.True(WebhookSignatures.VerifySlack(Secret, "1800000000", "{\"a\":1}", sig, Now));
    }

    [Fact]
    public void Slack_tampered_body_fails()
    {
        var sig = "v0=" + WebhookSignatures.Sign(Secret, "v0:1800000000:{\"a\":1}");
        Assert.False(WebhookSignatures.VerifySlack(Secret, "1800000000", "{\"a\":2}", sig, Now));
    }

    [Fact]
    public void Slack_replayed_old_request_fails()
    {
        var sig = "v0=" + WebhookSignatures.Sign(Secret, "v0:1800000000:{}");
        Assert.False(WebhookSignatures.VerifySlack(Secret, "1800000000", "{}", sig, Now.AddMinutes(6)));
    }

    [Fact]
    public void GitHub_signature_checked_and_empty_secret_rejected()
    {
        var sig = "sha256=" + WebhookSignatures.Sign(Secret, "{}");
        Assert.True(WebhookSignatures.VerifyGitHub(Secret, "{}", sig));
        Assert.False(WebhookSignatures.VerifyGitHub("other", "{}", sig));
        Assert.False(WebhookSignatures.VerifyGitHub("", "{}", sig));
    }
}

public class HandlerTests
{
    private readonly IIncidentRepository _repo = Substitute.For<IIncidentRepository>();
    private readonly IQueuePublisher _queue = Substitute.For<IQueuePublisher>();
    private readonly IGitHubClient _github = Substitute.For<IGitHubClient>();
    private readonly IIssueLookup _lookup = Substitute.For<IIssueLookup>();
    private readonly ISlackClient _slack = Substitute.For<ISlackClient>();
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    private static Incident Open(int? issue = null) => new()
    {
        Id = "Ev1", Title = "db down", Severity = Severity.Sev1, SlackChannel = "C1", SlackThreadTs = "1.1",
        ReportedAt = T0, GitHubIssueNumber = issue,
    };

    [Fact]
    public async Task Persist_new_incident_queues_github_issue()
    {
        _repo.TryCreateAsync(Arg.Any<Incident>()).Returns(true);
        await new PersistIncidentHandler(_repo, _queue).Handle(
            new(new IncidentReported("Ev1", "<@B> sev1 db down", "U1", "C1", "1.1", T0)), default);

        await _repo.Received().TryCreateAsync(Arg.Is<Incident>(i => i.Severity == Severity.Sev1 && i.Title == "db down"));
        await _queue.Received().SendAsync(Queues.GitHubCreate, new CreateGitHubIssue("Ev1"));
    }

    [Fact]
    public async Task Persist_duplicate_does_not_queue_again()
    {
        _repo.TryCreateAsync(Arg.Any<Incident>()).Returns(false);
        await new PersistIncidentHandler(_repo, _queue).Handle(
            new(new IncidentReported("Ev1", "sev1 db down", "U1", "C1", "1.1", T0)), default);

        await _queue.DidNotReceiveWithAnyArgs().SendAsync<CreateGitHubIssue>(default!, default!);
    }

    [Fact]
    public async Task OpenIssue_creates_issue_saves_lookup_and_notifies_slack()
    {
        _repo.GetAsync("Ev1").Returns(Open());
        _github.CreateIssueAsync(default!, default!, default!).ReturnsForAnyArgs(42);

        await new OpenGitHubIssueHandler(_repo, _github, _lookup, _slack).Handle(new("Ev1"), default);

        await _lookup.Received().SaveAsync(42, "Ev1");
        await _repo.Received().SetGitHubIssueAsync("Ev1", 42);
        await _slack.Received().PostAsync("C1", "1.1", Arg.Is<string>(s => s.Contains("#42")));
    }

    [Fact]
    public async Task OpenIssue_is_idempotent()
    {
        _repo.GetAsync("Ev1").Returns(Open(issue: 42));
        await new OpenGitHubIssueHandler(_repo, _github, _lookup, _slack).Handle(new("Ev1"), default);
        await _github.DidNotReceiveWithAnyArgs().CreateIssueAsync(default!, default!, default!);
    }

    [Fact]
    public async Task GitHub_comment_is_mirrored_to_slack_thread()
    {
        _lookup.FindAsync(42).Returns("Ev1");
        _repo.GetAsync("Ev1").Returns(Open(42));
        await new ApplyGitHubEventHandler(_lookup, _repo, _slack).Handle(
            new(new GitHubIssueEvent(42, GitHubIssueEvent.Comment, "rolled back", "alice", T0)), default);

        await _slack.Received().PostAsync("C1", "1.1", Arg.Is<string>(s => s.Contains("alice") && s.Contains("rolled back")));
    }

    [Fact]
    public async Task GitHub_close_resolves_incident_with_ttr()
    {
        _lookup.FindAsync(42).Returns("Ev1");
        _repo.GetAsync("Ev1").Returns(Open(42));
        await new ApplyGitHubEventHandler(_lookup, _repo, _slack).Handle(
            new(new GitHubIssueEvent(42, GitHubIssueEvent.Closed, null, null, T0.AddMinutes(90))), default);

        await _repo.Received().ResolveAsync("Ev1", T0.AddMinutes(90));
        await _slack.Received().PostAsync("C1", "1.1", Arg.Is<string>(s => s.Contains("90 min")));
    }

    [Fact]
    public async Task GitHub_event_for_unknown_issue_is_ignored()
    {
        await new ApplyGitHubEventHandler(_lookup, _repo, _slack).Handle(
            new(new GitHubIssueEvent(7, GitHubIssueEvent.Comment, "hi", "bob", T0)), default);
        await _slack.DidNotReceiveWithAnyArgs().PostAsync(default!, default!, default!);
    }
}

// Full mediator pipeline (ValidationBehavior) with mocked infrastructure.
public class PipelineTests
{
    private readonly IQueuePublisher _queue = Substitute.For<IQueuePublisher>();
    private readonly IMediator _mediator;

    public PipelineTests()
    {
        var services = new ServiceCollection().AddIncidentApplication();
        services.AddSingleton(_queue);
        services.AddSingleton(Substitute.For<IAnalyticsStore>());
        _mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();
    }

    [Fact]
    public async Task Report_without_description_is_rejected_before_queueing()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _mediator.Send(new ReportIncidentCommand("Ev1", "<@B> sev1", "U1", "C1", "1.1", DateTimeOffset.UtcNow)));
        await _queue.DidNotReceiveWithAnyArgs().SendAsync<IncidentReported>(default!, default!);
    }

    [Fact]
    public async Task Valid_report_is_queued()
    {
        await _mediator.Send(new ReportIncidentCommand("Ev1", "<@B> sev1 db down", "U1", "C1", "1.1", DateTimeOffset.UtcNow));
        await _queue.Received().SendAsync(Queues.IncidentIntake, Arg.Is<IncidentReported>(m => m.IncidentId == "Ev1"));
    }

    [Fact]
    public async Task Stats_query_rejects_bad_period() =>
        await Assert.ThrowsAsync<ValidationException>(() => _mediator.Send(new GetStatsQuery("2026-13")));
}
