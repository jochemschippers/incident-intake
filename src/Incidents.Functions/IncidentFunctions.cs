using Incidents.Application.Abstractions;
using Incidents.Application.Commands;
using Incidents.Application.Domain;
using Incidents.Infrastructure;
using MediatR;
using Microsoft.Azure.Functions.Worker;

namespace Incidents.Functions;

// Thin adapters: trigger -> mediator command. All logic lives in Application (unit-tested there).
// Queue triggers retry 5x (host.json maxDequeueCount), then the message lands in "<queue>-poison".
public class IncidentFunctions(IMediator mediator)
{
    // Fn1: Slack report -> write model (Cosmos "incidents"), then queue GitHub issue creation.
    [Function("PersistIncident")]
    public Task PersistIncident([QueueTrigger(Queues.IncidentIntake)] IncidentReported msg, CancellationToken ct) =>
        mediator.Send(new PersistIncidentCommand(msg), ct);

    // Fn2: open GitHub issue, store issue->incident lookup, confirm in the Slack thread.
    [Function("OpenGitHubIssue")]
    public Task OpenGitHubIssue([QueueTrigger(Queues.GitHubCreate)] CreateGitHubIssue msg, CancellationToken ct) =>
        mediator.Send(new OpenGitHubIssueCommand(msg.IncidentId), ct);

    // Fn3: GitHub comment -> Slack thread; GitHub issue closed -> incident resolved (MTTR).
    [Function("ApplyGitHubEvent")]
    public Task ApplyGitHubEvent([QueueTrigger(Queues.GitHubEvents)] GitHubIssueEvent msg, CancellationToken ct) =>
        mediator.Send(new ApplyGitHubEventCommand(msg), ct);

    // Fn4: Cosmos change feed of the write model -> analytics read model (CQRS projection).
    [Function("ProjectIncidents")]
    public async Task ProjectIncidents(
        [CosmosDBTrigger(CosmosNames.Database, CosmosNames.Incidents,
            Connection = "CosmosTrigger",
            LeaseContainerName = CosmosNames.Leases,
            CreateLeaseContainerIfNotExists = true)] IReadOnlyList<Incident> changes,
        CancellationToken ct)
    {
        foreach (var incident in changes)
            await mediator.Send(new ProjectIncidentCommand(incident), ct);
    }
}
