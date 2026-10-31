using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi.Models;
using SmartSolar.Api.Contracts;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace SmartSolar.Api.Extensions;

/// <summary>
/// Documents the error envelope (<c>ApiResponse&lt;object&gt;</c> with <c>error.code</c>, <c>message</c>, <c>details</c> and
/// <c>fieldErrors</c>) for the error statuses an endpoint can return, so the FE sees one schema for every failure:
/// 400 when it takes input, 401/403 when it needs a token, 404 when its route has parameters, 409 for writes,
/// 429 when it is rate limited and 503 for unavailable providers or features. Success schemas come from
/// [ProducesResponseType] on the action.
/// </summary>
public sealed class StandardErrorResponsesOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var errorSchema = context.SchemaGenerator.GenerateSchema(typeof(ApiResponse<object>), context.SchemaRepository);
        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        var method = context.ApiDescription.HttpMethod ?? "GET";
        var needsToken = metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any();

        if (operation.Parameters.Count > 0 || operation.RequestBody is not null) Add(operation, "400", "Validation failed (see error.fieldErrors).", errorSchema);
        if (needsToken)
        {
            Add(operation, "401", "Missing, invalid or expired token.", errorSchema);
            Add(operation, "403", "The caller's role or ownership does not allow this.", errorSchema);
        }
        if (context.ApiDescription.RelativePath?.Contains('{') == true) Add(operation, "404", "Not found (or not visible to the caller).", errorSchema);
        if (method is "POST" or "PUT" or "PATCH" or "DELETE") Add(operation, "409", "State, staleness or duplicate conflict.", errorSchema);
        if (metadata.OfType<EnableRateLimitingAttribute>().Any()) Add(operation, "429", "Rate limit or quota reached.", errorSchema);
        Add(operation, "503", "A provider, storage or feature is unavailable.", errorSchema);
    }

    private static void Add(OpenApiOperation operation, string status, string description, OpenApiSchema schema)
    {
        if (operation.Responses.ContainsKey(status)) return;   // an explicit [ProducesResponseType] wins
        operation.Responses[status] = new OpenApiResponse
        {
            Description = description,
            Content = { ["application/json"] = new OpenApiMediaType { Schema = schema } },
        };
    }
}
