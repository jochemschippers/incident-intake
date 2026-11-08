using FluentValidation;
using Incidents.Application.Behaviors;
using Microsoft.Extensions.DependencyInjection;

namespace Incidents.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddIncidentApplication(this IServiceCollection services)
    {
        var asm = typeof(DependencyInjection).Assembly;
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(asm);
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });
        services.AddValidatorsFromAssembly(asm);
        return services;
    }
}
