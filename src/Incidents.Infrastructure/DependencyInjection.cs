using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Queues;
using Incidents.Application.Abstractions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Incidents.Infrastructure;

public static class DependencyInjection
{
    // Config keys (env var form uses "__" instead of ":"):
    //   Storage:ConnectionString  (local/Azurite)   OR  Storage:AccountName  (Azure, Managed Identity)
    //   Cosmos:ConnectionString   (local/emulator)  OR  Cosmos:Endpoint      (Azure, Managed Identity)
    //   GitHub:Owner, GitHub:Repo, GitHub:Token, Slack:BotToken
    // With Managed Identity there are NO storage/DB keys anywhere; access is granted by Azure RBAC roles.
    public static IServiceCollection AddIncidentInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        TokenCredential credential = new DefaultAzureCredential();

        var storageCs = config["Storage:ConnectionString"];
        var account = config["Storage:AccountName"];
        var queueOptions = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
        services.AddSingleton(storageCs is { Length: > 0 }
            ? new QueueServiceClient(storageCs, queueOptions)
            : new QueueServiceClient(new Uri($"https://{account}.queue.core.windows.net"), credential, queueOptions));
        services.AddSingleton(storageCs is { Length: > 0 }
            ? new TableServiceClient(storageCs)
            : new TableServiceClient(new Uri($"https://{account}.table.core.windows.net"), credential));

        var cosmosOptions = new CosmosClientOptions
        {
            SerializerOptions = new CosmosSerializationOptions
            {
                PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
                IgnoreNullValues = true,
            },
        };
        var cosmosCs = config["Cosmos:ConnectionString"];
        if (cosmosCs?.Contains("localhost") == true) cosmosOptions.ConnectionMode = ConnectionMode.Gateway; // emulator
        services.AddSingleton(cosmosCs is { Length: > 0 }
            ? new CosmosClient(cosmosCs, cosmosOptions)
            : new CosmosClient(config["Cosmos:Endpoint"], credential, cosmosOptions));

        var gh = new GitHubOptions(config["GitHub:Owner"] ?? "", config["GitHub:Repo"] ?? "", config["GitHub:Token"] ?? "");
        var slack = new SlackOptions(config["Slack:BotToken"] ?? "");
        services.AddSingleton(gh);
        services.AddHttpClient<IGitHubClient, GitHubClient>(h => GitHubClient.Configure(h, gh));
        services.AddHttpClient<ISlackClient, SlackClient>(h => SlackClient.Configure(h, slack));

        services.AddSingleton<IQueuePublisher, StorageQueuePublisher>();
        services.AddSingleton<IIssueLookup, TableIssueLookup>();
        services.AddSingleton<IIncidentRepository, CosmosIncidentRepository>();
        services.AddSingleton<IAnalyticsStore, CosmosAnalyticsStore>();
        return services;
    }

    // Idempotent provisioning for local dev / first run. In Azure this is normally done by IaC (Bicep).
    public static async Task EnsureIncidentStorageAsync(this IServiceProvider sp)
    {
        var queues = sp.GetRequiredService<QueueServiceClient>();
        foreach (var q in new[] { Queues.IncidentIntake, Queues.GitHubCreate, Queues.GitHubEvents })
            await queues.GetQueueClient(q).CreateIfNotExistsAsync();

        await sp.GetRequiredService<TableServiceClient>().CreateTableIfNotExistsAsync(TableIssueLookup.TableName);

        var db = (await sp.GetRequiredService<CosmosClient>().CreateDatabaseIfNotExistsAsync(CosmosNames.Database)).Database;

        // Write model only does point reads -> index nothing but the id/PK. Saves RUs on every write.
        var incidents = new ContainerProperties(CosmosNames.Incidents, "/id")
        {
            IndexingPolicy = { ExcludedPaths = { new ExcludedPath { Path = "/*" } } },
        };
        await db.CreateContainerIfNotExistsAsync(incidents);
        await db.CreateContainerIfNotExistsAsync(new ContainerProperties(CosmosNames.Analytics, "/period"));
        await db.CreateContainerIfNotExistsAsync(new ContainerProperties(CosmosNames.Leases, "/id"));
    }
}
