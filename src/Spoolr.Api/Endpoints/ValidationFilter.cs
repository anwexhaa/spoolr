using System.ComponentModel.DataAnnotations;

namespace Spoolr.Api.Endpoints;

/// <summary>
/// Validates the request body against its data annotations before the handler runs.
/// </summary>
/// <remarks>
/// Applied as an endpoint filter rather than checked inside each handler, so a new endpoint
/// cannot forget it and every failure comes back in the same problem-details shape.
/// </remarks>
internal sealed class ValidationFilter<T> : IEndpointFilter
    where T : class
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        if (context.Arguments.OfType<T>().FirstOrDefault() is not { } model)
        {
            return TypedResults.BadRequest("A request body is required.");
        }

        var failures = new List<ValidationResult>();

        if (Validator.TryValidateObject(model, new ValidationContext(model), failures, validateAllProperties: true))
        {
            return await next(context);
        }

        var errors = failures
            .SelectMany(failure => failure.MemberNames.DefaultIfEmpty(string.Empty),
                (failure, member) => (Member: member, failure.ErrorMessage))
            .GroupBy(pair => pair.Member)
            .ToDictionary(
                group => group.Key,
                group => group.Select(pair => pair.ErrorMessage ?? "Invalid value.").ToArray());

        return TypedResults.ValidationProblem(errors);
    }
}

internal static class ValidationFilterExtensions
{
    /// <summary>Validates the request body of type <typeparamref name="T"/>.</summary>
    public static RouteHandlerBuilder ValidatingBody<T>(this RouteHandlerBuilder builder)
        where T : class =>
        builder.AddEndpointFilter<ValidationFilter<T>>()
            .ProducesValidationProblem();
}
