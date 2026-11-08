using Incidents.Application;
using Incidents.Infrastructure;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.Services
    .AddIncidentApplication()
    .AddIncidentInfrastructure(builder.Configuration);

var host = builder.Build();

if (builder.Environment.IsDevelopment())
    await host.Services.EnsureIncidentStorageAsync(); // containers must exist before the change feed trigger starts

host.Run();
