using FluentValidation;
using MediatR;

namespace Incidents.Application.Behaviors;

// Mediator pipeline: every command/query is validated before its handler runs.
public class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        foreach (var v in validators)
            await v.ValidateAndThrowAsync(request, ct);
        return await next();
    }
}
