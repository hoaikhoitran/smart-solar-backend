using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.Common.Messaging;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// Runs the real API pipeline (validation, envelope, rate limiting, controller)
/// against an in-memory SQLite database and a fake event publisher.
/// </summary>
public sealed class AuthApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private readonly Dictionary<string, string?> _settings;

    public AuthApiFactory(Dictionary<string, string?>? settings = null)
    {
        _connection.Open();
        _settings = settings ?? new Dictionary<string, string?>();
    }

    public FakeIntegrationEventPublisher Publisher { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // AddInfrastructure requires a non-empty connection string; the real
        // provider is replaced below, so this value is never used to connect.
        builder.UseSetting("ConnectionStrings:DefaultConnection", "Host=localhost;Database=unused");
        builder.UseSetting("Frontend:EmailVerificationUrl", "https://app.test/verify-email");
        builder.UseSetting("Frontend:PasswordResetUrl", "https://app.test/reset-password");

        // Test-only signing key; production supplies its own from the environment.
        builder.UseSetting("Jwt:Issuer", "SmartSolar");
        builder.UseSetting("Jwt:Audience", "SmartSolarClients");
        builder.UseSetting("Jwt:SigningKey", "integration-test-signing-key-32-bytes!!");
        builder.UseSetting("Jwt:AccessTokenLifetimeMinutes", "15");

        // Generous limits by default so unrelated tests are not throttled;
        // the rate-limit test supplies its own values.
        builder.UseSetting("RateLimiting:Register:PermitLimit", "1000");
        builder.UseSetting("RateLimiting:VerifyEmail:PermitLimit", "1000");
        builder.UseSetting("RateLimiting:ResendVerification:PermitLimit", "1000");
        builder.UseSetting("RateLimiting:Login:PermitLimit", "1000");
        builder.UseSetting("RateLimiting:Refresh:PermitLimit", "1000");
        builder.UseSetting("RateLimiting:ForgotPassword:PermitLimit", "1000");
        builder.UseSetting("RateLimiting:ResetPassword:PermitLimit", "1000");
        builder.UseSetting("RateLimiting:ChangePassword:PermitLimit", "1000");

        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.AddDbContext<AppDbContext>(options => options
                .UseSqlite(_connection, sqlite => sqlite
                    .ExecutionStrategy(dependencies => new TestRetryingExecutionStrategy(dependencies))));

            services.RemoveAll<IIntegrationEventPublisher>();
            services.AddSingleton<IIntegrationEventPublisher>(Publisher);

            // Create the schema before the host starts, because startup seeding
            // writes roles into this database.
            var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        });
    }

    public AppDbContext CreateDbContext()
        => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _connection.Dispose();
        }
    }
}

internal static class ServiceCollectionTestExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(T)).ToList())
        {
            services.Remove(descriptor);
        }
    }
}
