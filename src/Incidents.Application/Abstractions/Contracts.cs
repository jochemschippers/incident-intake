using Incidents.Application.Domain;

namespace Incidents.Application.Abstractions;

public static class Queues
{
    public const string IncidentIntake = "incident-intake";
    public const string GitHubCreate = "github-create";
    public const string GitHubEvents = "github-events";
}

// Queue message contracts (JSON). Services only share these, not each other's internals.
public record IncidentReported(string IncidentId, string Text, string User, string Channel, string ThreadTs, DateTimeOffset At);
public record CreateGitHubIssue(string IncidentId);
public record GitHubIssueEvent(int IssueNumber, string Action, string? CommentBody, string? CommentAuthor, DateTimeOffset At)
{
    public const string Comment = "comment";
    public const string Closed = "closed";
}

public interface IQueuePublisher
{
    Task SendAsync<T>(string queue, T message, CancellationToken ct = default);
}

public interface IIncidentRepository
{
    /// <returns>false when an incident with this id already exists (duplicate delivery).</returns>
    Task<bool> TryCreateAsync(Incident incident, CancellationToken ct = default);
    Task<Incident?> GetAsync(string id, CancellationToken ct = default);
    Task SetGitHubIssueAsync(string id, int issueNumber, CancellationToken ct = default);
    Task ResolveAsync(string id, DateTimeOffset resolvedAt, CancellationToken ct = default);
}

public interface IAnalyticsStore
{
    Task UpsertAsync(IncidentFact fact, CancellationToken ct = default);
    Task<IReadOnlyList<IncidentFact>> ListAsync(string period, Severity? severity, CancellationToken ct = default);
    Task<IReadOnlyList<SeverityStats>> StatsAsync(string period, CancellationToken ct = default);
}

public interface IIssueLookup
{
    Task SaveAsync(int issueNumber, string incidentId, CancellationToken ct = default);
    Task<string?> FindAsync(int issueNumber, CancellationToken ct = default);
}

public interface IGitHubClient
{
    Task<int> CreateIssueAsync(string title, string body, IEnumerable<string> labels, CancellationToken ct = default);
}

public interface ISlackClient
{
    Task PostAsync(string channel, string threadTs, string text, CancellationToken ct = default);
}
