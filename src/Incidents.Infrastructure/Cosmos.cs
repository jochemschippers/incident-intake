using System.Net;
using Incidents.Application.Abstractions;
using Incidents.Application.Domain;
using Microsoft.Azure.Cosmos;

namespace Incidents.Infrastructure;

public static class CosmosNames
{
    public const string Database = "incidents-db";
    public const string Incidents = "incidents";          // write model, PK /id
    public const string Analytics = "incident-analytics"; // read model,  PK /period
    public const string Leases = "leases";                // change feed checkpoints (Functions)
}

// Write model. PK = /id (Slack event_id):
//  - high cardinality -> writes spread evenly over physical partitions, no hot partition
//  - every write-side read is a point read (id + PK) -> ~1 RU
//  - create on an existing id -> 409 -> free deduplication of Slack retries / queue redeliveries
// Updates use PATCH on single fields so concurrent workers never overwrite each other (no lost update).
public class CosmosIncidentRepository(CosmosClient client) : IIncidentRepository
{
    private readonly Container _c = client.GetContainer(CosmosNames.Database, CosmosNames.Incidents);

    public async Task<bool> TryCreateAsync(Incident incident, CancellationToken ct = default)
    {
        try
        {
            await _c.CreateItemAsync(incident, new PartitionKey(incident.Id), cancellationToken: ct);
            return true;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    public async Task<Incident?> GetAsync(string id, CancellationToken ct = default)
    {
        try
        {
            return (await _c.ReadItemAsync<Incident>(id, new PartitionKey(id), cancellationToken: ct)).Resource;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task SetGitHubIssueAsync(string id, int issueNumber, CancellationToken ct = default) =>
        _c.PatchItemAsync<Incident>(id, new PartitionKey(id),
            [PatchOperation.Set("/gitHubIssueNumber", issueNumber)], cancellationToken: ct);

    public Task ResolveAsync(string id, DateTimeOffset resolvedAt, CancellationToken ct = default) =>
        _c.PatchItemAsync<Incident>(id, new PartitionKey(id),
            [PatchOperation.Set("/status", (int)IncidentStatus.Resolved), PatchOperation.Set("/resolvedAt", resolvedAt)],
            cancellationToken: ct);
}

// Read model. PK = /period (yyyy-MM): every dashboard/analytics query is "for month X"
// -> always a single-partition query, never a fan-out over all partitions.
// One doc per incident; upsert keeps the change-feed projection idempotent.
public class CosmosAnalyticsStore(CosmosClient client) : IAnalyticsStore
{
    private readonly Container _c = client.GetContainer(CosmosNames.Database, CosmosNames.Analytics);

    public Task UpsertAsync(IncidentFact fact, CancellationToken ct = default) =>
        _c.UpsertItemAsync(fact, new PartitionKey(fact.Period), cancellationToken: ct);

    public Task<IReadOnlyList<IncidentFact>> ListAsync(string period, Severity? severity, CancellationToken ct = default)
    {
        var q = severity is null
            ? new QueryDefinition("SELECT * FROM c ORDER BY c.reportedAt DESC")
            : new QueryDefinition("SELECT * FROM c WHERE c.severity = @sev ORDER BY c.reportedAt DESC")
                .WithParameter("@sev", (int)severity);
        return QueryAsync<IncidentFact>(q, period, ct);
    }

    public async Task<IReadOnlyList<SeverityStats>> StatsAsync(string period, CancellationToken ct = default)
    {
        // ponytail: aggregates in memory over one month's partition (projection of 2 small fields).
        // Ceiling: ~10k incidents/month. Upgrade: server-side GROUP BY or a pre-aggregated stats doc.
        var rows = await QueryAsync<StatRow>(
            new QueryDefinition("SELECT c.severity, c.minutesToResolve FROM c"), period, ct);

        return rows.GroupBy(r => r.Severity)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var resolved = g.Where(r => r.MinutesToResolve is not null).Select(r => r.MinutesToResolve!.Value).ToList();
                return new SeverityStats(g.Key, g.Count(), resolved.Count,
                    resolved.Count == 0 ? null : Math.Round(resolved.Average(), 1));
            })
            .ToList();
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(QueryDefinition q, string period, CancellationToken ct)
    {
        var it = _c.GetItemQueryIterator<T>(q, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(period) });
        var list = new List<T>();
        while (it.HasMoreResults)
            list.AddRange(await it.ReadNextAsync(ct));
        return list;
    }

    private record StatRow(Severity Severity, double? MinutesToResolve);
}
