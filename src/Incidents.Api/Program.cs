using System.Text.Json.Serialization;
using FluentValidation;
using Incidents.Api;
using Incidents.Application;
using Incidents.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddIncidentApplication()
    .AddIncidentInfrastructure(builder.Configuration)
    .AddScoped<ApiKeyFilter>()
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await app.Services.EnsureIncidentStorageAsync(); // Azure: provisioned by IaC, MI has data-plane rights only
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Business-rule violations (mediator ValidationBehavior) -> 400 instead of 500.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (ValidationException e)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(e.Errors.Select(x => new { x.PropertyName, x.ErrorMessage }));
    }
});

app.MapControllers(); // HTTPS: enforced by App Service "HTTPS only" / the dev tunnel, not by a redirect (webhook POSTs don't follow redirects)
app.Run();
