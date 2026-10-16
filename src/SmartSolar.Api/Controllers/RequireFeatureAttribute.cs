using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Common.Features;
using SmartSolar.Modules.Common.Results;

namespace SmartSolar.Api.Controllers;

/// <summary>
/// Returns 503 FEATURE_PENDING_MIGRATION unless at least one of the named features is enabled
/// (<see cref="FeatureOptions"/>), i.e. until the tables of that feature have been created by the
/// manually approved migration.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireFeatureAttribute(params string[] anyOf) : Attribute, IAsyncActionFilter
{
    public IReadOnlyList<string> Features { get; } = anyOf;

    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var features = context.HttpContext.RequestServices.GetRequiredService<FeatureOptions>();
        if (Features.Any(features.IsEnabled)) return next();
        context.Result = new ObjectResult(ApiResponse<object>.Failure(
            new ApiError(CommonErrorCodes.FeaturePendingMigration, "This feature is not enabled until its database migration has been applied."),
            context.HttpContext.GetTraceId())) { StatusCode = StatusCodes.Status503ServiceUnavailable };
        return Task.CompletedTask;
    }
}
