using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using SmartSolar.Api.Contracts;
using SmartSolar.Infrastructure.Email;
using SmartSolar.Infrastructure.Persistence.Seed;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Infrastructure.Security;
using SmartSolar.Modules.Identity.Contracts.Security;
using SmartSolar.Modules.Identity.EmailVerification;
using SmartSolar.Modules.Identity.Login;
using SmartSolar.Modules.Identity.Options;
using SmartSolar.Modules.Identity.PasswordRecovery;
using SmartSolar.Modules.Identity.RefreshTokens;
using SmartSolar.Modules.Identity.Register;
using SmartSolar.Modules.Identity.Security;

namespace SmartSolar.Api.Extensions;

public static class IdentityModuleExtensions
{
    public static IServiceCollection AddIdentityModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var verificationOptions = new EmailVerificationOptions();
        configuration.GetSection(EmailVerificationOptions.SectionName).Bind(verificationOptions);

        if (verificationOptions.LifetimeMinutes <= 0)
        {
            throw new InvalidOperationException(
                $"'{EmailVerificationOptions.SectionName}:LifetimeMinutes' must be greater than zero.");
        }

        services.AddSingleton(verificationOptions);

        var frontendOptions = new FrontendOptions();
        configuration.GetSection(FrontendOptions.SectionName).Bind(frontendOptions);

        if (!Uri.TryCreate(frontendOptions.EmailVerificationUrl, UriKind.Absolute, out _))
        {
            // Without this, verification emails would carry unusable links and
            // nothing would fail until customers complained.
            throw new InvalidOperationException(
                "'Frontend:EmailVerificationUrl' must be configured as an absolute URL.");
        }

        if (!Uri.TryCreate(frontendOptions.PasswordResetUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                "'Frontend:PasswordResetUrl' must be configured as an absolute URL.");
        }

        services.AddSingleton(frontendOptions);

        var passwordResetOptions = new PasswordResetOptions();
        configuration.GetSection(PasswordResetOptions.SectionName).Bind(passwordResetOptions);

        if (passwordResetOptions.LifetimeMinutes <= 0)
        {
            throw new InvalidOperationException(
                $"'{PasswordResetOptions.SectionName}:LifetimeMinutes' must be greater than zero.");
        }

        services.AddSingleton(passwordResetOptions);

        var refreshTokenOptions = new RefreshTokenOptions();
        configuration.GetSection(RefreshTokenOptions.SectionName).Bind(refreshTokenOptions);

        if (refreshTokenOptions.LifetimeDays <= 0)
        {
            throw new InvalidOperationException(
                $"'{RefreshTokenOptions.SectionName}:LifetimeDays' must be greater than zero.");
        }

        services.AddSingleton(refreshTokenOptions);

        // Constructing the token service here means a missing or weak signing
        // key fails startup rather than the first login.
        var jwtOptions = ReadJwtOptions(configuration);
        services.AddSingleton(jwtOptions);
        services.AddSingleton<IAccessTokenService>(new JwtAccessTokenService(jwtOptions));

        services.AddSingleton<EmailVerificationTokenFactory>();
        services.AddSingleton<SecureTokenFactory>();

        services.AddScoped<RegisterHandler>();
        services.AddScoped<VerifyEmailHandler>();
        services.AddScoped<ResendVerificationHandler>();
        services.AddScoped<RefreshTokenIssuer>();
        services.AddScoped<LoginHandler>();
        services.AddScoped<RefreshTokenHandler>();
        services.AddScoped<LogoutHandler>();
        services.AddScoped<ForgotPasswordHandler>();
        services.AddScoped<ResetPasswordHandler>();
        services.AddScoped<ChangePasswordHandler>();

        services.AddValidatorsFromAssemblyContaining<RegisterCommandValidator>();

        return services;
    }

    /// <summary>Reads and validates the JWT settings, naming keys but never the key material.</summary>
    public static JwtOptions ReadJwtOptions(IConfiguration configuration)
    {
        var jwtOptions = new JwtOptions();
        configuration.GetSection(JwtOptions.SectionName).Bind(jwtOptions);

        if (string.IsNullOrWhiteSpace(jwtOptions.Issuer) || string.IsNullOrWhiteSpace(jwtOptions.Audience))
        {
            throw new InvalidOperationException(
                $"'{JwtOptions.SectionName}:Issuer' and '{JwtOptions.SectionName}:Audience' must be configured.");
        }

        return jwtOptions;
    }

    /// <summary>
    /// Makes model-binding failures (malformed JSON, wrong types, empty bodies)
    /// use the same envelope as everything else.
    /// </summary>
    public static IServiceCollection AddEnvelopedModelValidation(this IServiceCollection services)
    {
        services.Configure<ApiBehaviorOptions>(options =>
        {
            // Let bare client errors (415, 405) fall through to the status-code
            // handler instead of becoming ProblemDetails.
            options.SuppressMapClientErrors = true;

            options.InvalidModelStateResponseFactory = context =>
            {
                // Several model-state entries can map to the same field name,
                // so group before building the details dictionary.
                var details = context.ModelState
                    .Where(entry => entry.Value?.Errors.Count > 0)
                    .GroupBy(entry => string.IsNullOrEmpty(entry.Key) ? "request" : entry.Key)
                    .ToDictionary(
                        group => group.Key,
                        group => group
                            .SelectMany(entry => entry.Value!.Errors)
                            .Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage)
                                ? "The value is invalid."
                                : error.ErrorMessage)
                            .ToArray());

                var envelope = ApiResponse<object>.Failure(
                    new ApiError(
                        AuthErrorCodes.ValidationFailed,
                        "One or more fields are invalid.",
                        details),
                    context.HttpContext.GetTraceId());

                return new BadRequestObjectResult(envelope);
            };
        });

        return services;
    }

    /// <summary>
    /// Wraps framework responses that carry no body of their own (404, 415, 405)
    /// so a client never receives a bare status code.
    /// </summary>
    public static WebApplication UseEnvelopedStatusCodes(this WebApplication app)
    {
        app.UseStatusCodePages(async context =>
        {
            var response = context.HttpContext.Response;

            var (code, message) = response.StatusCode switch
            {
                StatusCodes.Status401Unauthorized => (
                    AuthErrorCodes.Unauthorized, "Authentication is required for this request."),
                StatusCodes.Status403Forbidden => (
                    AuthErrorCodes.Forbidden, "You do not have access to this resource."),
                StatusCodes.Status404NotFound => (
                    "RESOURCE_NOT_FOUND", "The request could not be processed."),
                _ => ("REQUEST_NOT_ACCEPTABLE", "The request could not be processed.")
            };

            var envelope = ApiResponse<object>.Failure(
                new ApiError(code, message),
                context.HttpContext.GetTraceId());

            response.ContentType = "application/json";

            await response.WriteAsync(JsonSerializer.Serialize(
                envelope,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        });

        return app;
    }

    /// <summary>
    /// Bootstraps the fixed system roles. Startup continues when the database is
    /// unreachable; registration then fails loudly until the roles exist.
    /// </summary>
    public static async Task SeedSystemRolesAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<SystemRoleSeeder>>();

        try
        {
            var seeder = scope.ServiceProvider.GetRequiredService<SystemRoleSeeder>();
            await seeder.SeedAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogCritical(
                ex,
                "System role bootstrap failed. Registration will fail until the roles exist.");
        }
    }

    /// <summary>
    /// States at startup whether verification emails can actually be delivered,
    /// so a half-configured environment is never mistaken for a working one.
    /// </summary>
    public static WebApplication LogEmailDeliveryReadiness(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("SmartSolar.EmailDelivery");

        var state = EmailDeliveryReadiness.Describe(
            app.Configuration.GetValue<bool>("RabbitMq:Enabled"),
            app.Configuration.GetValue<bool>($"{EmailOptions.SectionName}:{nameof(EmailOptions.Enabled)}"));

        if (state.CanDeliver)
        {
            logger.LogInformation("Verification email delivery is enabled.");
        }
        else
        {
            logger.LogWarning("{Warning}", state.Warning);
        }

        return app;
    }
}
