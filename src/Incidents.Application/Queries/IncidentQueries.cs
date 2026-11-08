using FluentValidation;
using Incidents.Application.Abstractions;
using Incidents.Application.Domain;
using MediatR;
using Severity = Incidents.Application.Domain.Severity;

namespace Incidents.Application.Queries;

// Point read on the write model (id == partition key -> ~1 RU).
public record GetIncidentQuery(string Id) : IRequest<Incident?>;

public class GetIncidentHandler(IIncidentRepository repo) : IRequestHandler<GetIncidentQuery, Incident?>
{
    public Task<Incident?> Handle(GetIncidentQuery q, CancellationToken ct) => repo.GetAsync(q.Id, ct);
}

// Below: reads hit the analytics read model only -> single-partition queries on /period.
public record ListIncidentsQuery(string Period, Severity? Severity) : IRequest<IReadOnlyList<IncidentFact>>;

public class ListIncidentsHandler(IAnalyticsStore analytics) : IRequestHandler<ListIncidentsQuery, IReadOnlyList<IncidentFact>>
{
    public Task<IReadOnlyList<IncidentFact>> Handle(ListIncidentsQuery q, CancellationToken ct) =>
        analytics.ListAsync(q.Period, q.Severity, ct);
}

public record GetStatsQuery(string Period) : IRequest<IReadOnlyList<SeverityStats>>;

public class GetStatsHandler(IAnalyticsStore analytics) : IRequestHandler<GetStatsQuery, IReadOnlyList<SeverityStats>>
{
    public Task<IReadOnlyList<SeverityStats>> Handle(GetStatsQuery q, CancellationToken ct) =>
        analytics.StatsAsync(q.Period, ct);
}

public class ListIncidentsValidator : AbstractValidator<ListIncidentsQuery>
{
    public ListIncidentsValidator() => RuleFor(q => q.Period).Matches(Period.Pattern).WithMessage(Period.Message);
}

public class GetStatsValidator : AbstractValidator<GetStatsQuery>
{
    public GetStatsValidator() => RuleFor(q => q.Period).Matches(Period.Pattern).WithMessage(Period.Message);
}

internal static class Period
{
    public const string Pattern = @"^\d{4}-(0[1-9]|1[0-2])$";
    public const string Message = "period must be yyyy-MM, e.g. 2026-11";
}
