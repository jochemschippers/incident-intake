using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Queues;
using Incidents.Application.Abstractions;

namespace Incidents.Infrastructure;

// Azure Storage Queue: load leveling between the webhook endpoints and the slow work (DB, GitHub, Slack).
// Base64 encoding = what the Functions QueueTrigger expects by default.
public class StorageQueuePublisher(QueueServiceClient queues) : IQueuePublisher
{
    public Task SendAsync<T>(string queue, T message, CancellationToken ct = default) =>
        queues.GetQueueClient(queue).SendMessageAsync(JsonSerializer.Serialize(message), ct);
}

// Directory-based lookup (Table Storage): GitHub issue number -> incident id.
// PK = "owner_repo" (one partition per repo; '/' is not allowed in keys), RK = issue number.
// PK + RK known -> point lookup, cheapest possible Table Storage read.
public class TableIssueLookup(TableServiceClient tables, GitHubOptions gh) : IIssueLookup
{
    public const string TableName = "IssueLookup";
    private readonly TableClient _t = tables.GetTableClient(TableName);
    private string Pk => $"{gh.Owner}_{gh.Repo}";

    public Task SaveAsync(int issueNumber, string incidentId, CancellationToken ct = default) =>
        _t.UpsertEntityAsync(new TableEntity(Pk, issueNumber.ToString()) { ["IncidentId"] = incidentId }, cancellationToken: ct);

    public async Task<string?> FindAsync(int issueNumber, CancellationToken ct = default)
    {
        NullableResponse<TableEntity> r = await _t.GetEntityIfExistsAsync<TableEntity>(Pk, issueNumber.ToString(), cancellationToken: ct);
        return r.HasValue ? r.Value!.GetString("IncidentId") : null;
    }
}
