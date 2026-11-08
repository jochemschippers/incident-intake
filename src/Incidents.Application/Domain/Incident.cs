namespace Incidents.Application.Domain;

public enum Severity { Sev1 = 1, Sev2 = 2, Sev3 = 3, Sev4 = 4 }

public enum IncidentStatus { Open, Resolved }

// Write model. Stored in Cosmos container "incidents", partition key /id.
public class Incident
{
    public string Id { get; set; } = "";               // Slack event_id -> natural idempotency key
    public string Title { get; set; } = "";
    public Severity Severity { get; set; }
    public IncidentStatus Status { get; set; }
    public string ReportedBy { get; set; } = "";
    public string SlackChannel { get; set; } = "";
    public string SlackThreadTs { get; set; } = "";
    public int? GitHubIssueNumber { get; set; }
    public DateTimeOffset ReportedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    public double? MinutesToResolve =>
        ResolvedAt is { } r ? Math.Round((r - ReportedAt).TotalMinutes, 1) : null;

    public void Resolve(DateTimeOffset at)
    {
        if (Status == IncidentStatus.Resolved) return; // idempotent: GitHub may redeliver "closed"
        Status = IncidentStatus.Resolved;
        ResolvedAt = at;
    }
}

// Read model. Stored in Cosmos container "incident-analytics", partition key /period (yyyy-MM).
// One doc per incident, upserted from the change feed -> idempotent projection.
public record IncidentFact(
    string Id,
    string Period,
    string Title,
    Severity Severity,
    IncidentStatus Status,
    int? GitHubIssueNumber,
    DateTimeOffset ReportedAt,
    DateTimeOffset? ResolvedAt,
    double? MinutesToResolve)
{
    public static string PeriodOf(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM");

    public static IncidentFact From(Incident i) => new(
        i.Id, PeriodOf(i.ReportedAt), i.Title, i.Severity, i.Status,
        i.GitHubIssueNumber, i.ReportedAt, i.ResolvedAt, i.MinutesToResolve);
}

public record SeverityStats(Severity Severity, int Total, int Resolved, double? MttrMinutes);
