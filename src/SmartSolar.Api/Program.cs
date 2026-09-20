using SmartSolar.Api.Extensions;
using SmartSolar.Api.Middlewares;
using SmartSolar.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddIdentityModule(builder.Configuration);
builder.Services.AddAuthRateLimiting(builder.Configuration);
builder.Services.AddForwardedHeaders(builder.Configuration);
builder.Services.AddControllers();
builder.Services.AddEnvelopedModelValidation();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

await app.SeedSystemRolesAsync();

app.LogEmailDeliveryReadiness();

// Configure the HTTP request pipeline.
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseEnvelopedStatusCodes();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// Must run before rate limiting so the client IP partition is the real client.
app.UseForwardedHeaders();

app.UseRateLimiter();

app.UseAuthorization();

app.MapControllers();

app.Run();

/// <summary>Exposed so integration tests can host the real pipeline.</summary>
public partial class Program
{
}
