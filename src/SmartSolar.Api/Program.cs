using Microsoft.OpenApi.Models;
using SmartSolar.Api.Extensions;
using SmartSolar.Api.Middlewares;
using SmartSolar.Infrastructure;

// Controlled administrative catalog import (CLI mode of the host; never an HTTP endpoint or a startup task).
if (SmartSolar.Api.Cli.AdministrativeCatalogCli.IsCommand(args))
{
    Environment.ExitCode = await SmartSolar.Api.Cli.AdministrativeCatalogCli.RunAsync(args, Console.Out, CancellationToken.None);
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? [];

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddIdentityModule(builder.Configuration);
builder.Services.AddJwtAuthentication(IdentityModuleExtensions.ReadJwtOptions(builder.Configuration));
builder.Services.AddCatalogModule(builder.Configuration);
builder.Services.AddAuthRateLimiting(builder.Configuration);
builder.Services.AddCatalogRateLimiting(builder.Configuration);
builder.Services.AddPreSurveyModule();
builder.Services.AddSolarSimulationModule(builder.Configuration);
builder.Services.AddSimulationRateLimiting(builder.Configuration);
builder.Services.AddSchedulingModule(builder.Configuration);
builder.Services.AddSchedulingRateLimiting(builder.Configuration);
builder.Services.AddAdminUsersRateLimiting(builder.Configuration);
builder.Services.AddFeatureFlagsAndFileStorage(builder.Configuration);
builder.Services.AddInstallationWorkflow(builder.Configuration);
builder.Services.AddInstallationWorkflowRateLimiting(builder.Configuration);
builder.Services.AddAfterSales(builder.Configuration);
builder.Services.AddKnowledgeBase(builder.Configuration);
builder.Services.AddKnowledgeBaseRateLimiting(builder.Configuration);
builder.Services.AddSystemSettingsModule(builder.Configuration);
builder.Services.AddSystemSettingsRateLimiting(builder.Configuration);
builder.Services.AddForwardedHeaders(builder.Configuration);
// Enums are written as stable UPPER_SNAKE_CASE strings (e.g. "PENDING", "ROOFTOP"), like the other status codes of the API.
// Reading still accepts the numeric values older clients send, so requests stay backwards compatible.
builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: true)));
builder.Services.AddEnvelopedModelValidation();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
     options.CustomSchemaIds(
        type => type.FullName?.Replace('+', '.') ?? type.Name
    );
    options.OperationFilter<SmartSolar.Api.Extensions.StandardErrorResponsesOperationFilter>();

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter your JWT access token."
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});
builder.Services.AddSingleton<SmartSolar.Api.Middlewares.ISchemaMigrationProbe, SmartSolar.Api.Middlewares.EfSchemaMigrationProbe>();
builder.Services.AddSingleton<SmartSolar.Api.Middlewares.SchemaReadiness>();
var app = builder.Build();

if (app.Services.GetRequiredService<SmartSolar.Modules.Common.Features.FeatureOptions>().UsesLegacyFlag)
{
    app.Logger.LogWarning("'Features:Flow2Enabled' is deprecated; set 'Features:InstallationWorkflowEnabled' and 'Features:KnowledgeBaseEnabled' instead.");
}

await app.SeedSystemRolesAsync();

app.LogEmailDeliveryReadiness();

// Configure the HTTP request pipeline.
app.UseMiddleware<ExceptionHandlingMiddleware>();
// Before anything touches the database: an older schema answers 503 SCHEMA_NOT_READY instead of failing per query.
app.UseMiddleware<SmartSolar.Api.Middlewares.SchemaReadinessMiddleware>();
app.UseEnvelopedStatusCodes();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// Must run before rate limiting so the client IP partition is the real client.
app.UseForwardedHeaders();
app.UseCors("Frontend");

// Authentication must run before authorization so [Authorize] sees the principal.
app.UseAuthentication();

// After authentication so per-user policies can partition by the JWT subject;
// anonymous auth endpoints still partition by client IP.
app.UseRateLimiter();

app.UseAuthorization();

app.MapControllers();

app.Run();

/// <summary>Exposed so integration tests can host the real pipeline.</summary>
public partial class Program
{
}
