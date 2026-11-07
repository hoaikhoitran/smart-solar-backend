using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.Swagger;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.OpenApi;

/// <summary>The generated OpenAPI document: it builds without schema-id collisions and documents FE-used responses and errors.</summary>
public sealed class OpenApiDocumentTests
{
    private static OpenApiDocument Document()
    {
        using var factory = new AuthApiFactory();
        return factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
    }

    private static string SchemaIdOf(OpenApiResponse response) => response.Content["application/json"].Schema.Reference.Id;

    [Fact]
    public void The_document_builds_and_serializes_as_openapi_json()
    {
        var document = Document();

        var json = document.SerializeAsJson(Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_0);

        Assert.Contains("\"openapi\": \"3.0", json);
        Assert.True(document.Paths.Count > 100);
    }

    [Theory]
    [InlineData("/api/technicians/me/appointments", OperationType.Get, "200", "TechnicianAppointmentListItem")]
    [InlineData("/api/survey-requests/my", OperationType.Get, "200", "MySurveyRequestItem")]
    [InlineData("/api/customers/me/sites", OperationType.Get, "200", "CustomerSiteListItem")]
    [InlineData("/api/customers/me/pre-surveys", OperationType.Get, "200", "CustomerPreSurveyListItem")]
    [InlineData("/api/pre-surveys/{preSurveyId}/simulations/{simulationId}", OperationType.Get, "200", "SimulationDetail")]
    [InlineData("/api/administrative-units/communes", OperationType.Get, "200", "CommuneView")]
    [InlineData("/api/files/presigned-upload", OperationType.Post, "200", "UploadUrlView")]
    [InlineData("/api/admin/products", OperationType.Get, "200", "ProductResponse")]
    public void Priority_endpoints_document_their_success_schema(string path, OperationType method, string status, string schemaPart)
    {
        var document = Document();

        var response = document.Paths[path].Operations[method].Responses[status];

        Assert.Contains(schemaPart, SchemaIdOf(response));
    }

    [Fact]
    public void Errors_use_the_envelope_schema_with_field_errors_and_enums_are_strings()
    {
        var document = Document();
        var operation = document.Paths["/api/admin/products"].Operations[OperationType.Get];

        foreach (var status in new[] { "400", "401", "403", "429", "503" })
            Assert.Contains("ApiResponse", SchemaIdOf(operation.Responses[status]));
        var error = document.Components.Schemas.Single(s => s.Key.EndsWith("Contracts.ApiError")).Value;
        Assert.Contains("fieldErrors", error.Properties.Keys);
        var surfaceType = document.Components.Schemas.Single(s => s.Key.EndsWith("InstallationSurfaceType")).Value;
        Assert.Equal("string", surfaceType.Type);
        Assert.Contains(surfaceType.Enum, e => ((Microsoft.OpenApi.Any.OpenApiString)e).Value == "ROOFTOP");
    }
}
