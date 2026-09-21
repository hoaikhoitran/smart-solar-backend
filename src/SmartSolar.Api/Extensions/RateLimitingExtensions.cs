using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts;
using SmartSolar.Modules.Identity.Constants;

namespace SmartSolar.Api.Extensions;

public static class RateLimitingExtensions
{
    public const string RegisterPolicy = "auth-register";
    public const string VerifyEmailPolicy = "auth-verify-email";
    public const string ResendVerificationPolicy = "auth-resend-verification";
    public const string LoginPolicy = "auth-login";
    public const string RefreshPolicy = "auth-refresh";
    public const string ForgotPasswordPolicy = "auth-forgot-password";
    public const string ResetPasswordPolicy = "auth-reset-password";
    public const string ChangePasswordPolicy = "auth-change-password";

    public static IServiceCollection AddAuthRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(RegisterPolicy, context => FixedWindowByClient(
                context, configuration, "RateLimiting:Register", permitLimit: 5, windowMinutes: 10));

            options.AddPolicy(VerifyEmailPolicy, context => FixedWindowByClient(
                context, configuration, "RateLimiting:VerifyEmail", permitLimit: 20, windowMinutes: 10));

            options.AddPolicy(ResendVerificationPolicy, context => FixedWindowByClient(
                context, configuration, "RateLimiting:ResendVerification", permitLimit: 3, windowMinutes: 15));

            options.AddPolicy(LoginPolicy, context => FixedWindowByClient(
                context, configuration, "RateLimiting:Login", permitLimit: 10, windowMinutes: 10));

            options.AddPolicy(RefreshPolicy, context => FixedWindowByClient(
                context, configuration, "RateLimiting:Refresh", permitLimit: 30, windowMinutes: 10));

            options.AddPolicy(ForgotPasswordPolicy, context => FixedWindowByClient(
                context, configuration, "RateLimiting:ForgotPassword", permitLimit: 3, windowMinutes: 15));

            options.AddPolicy(ResetPasswordPolicy, context => FixedWindowByClient(
                context, configuration, "RateLimiting:ResetPassword", permitLimit: 10, windowMinutes: 15));

            options.AddPolicy(ChangePasswordPolicy, context => FixedWindowByClient(
                context, configuration, "RateLimiting:ChangePassword", permitLimit: 10, windowMinutes: 15));

            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.ContentType = "application/json";

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString();
                }

                var envelope = ApiResponse<object>.Failure(
                    new ApiError(
                        AuthErrorCodes.TooManyRequests,
                        "Too many requests. Please try again later."),
                    context.HttpContext.GetTraceId());

                await context.HttpContext.Response.WriteAsync(
                    JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    cancellationToken);
            };
        });

        return services;
    }

    private static RateLimitPartition<string> FixedWindowByClient(
        HttpContext context,
        IConfiguration configuration,
        string sectionName,
        int permitLimit,
        int windowMinutes)
    {
        var section = configuration.GetSection(sectionName);
        var limit = section.GetValue<int?>("PermitLimit") ?? permitLimit;
        var window = section.GetValue<int?>("WindowMinutes") ?? windowMinutes;

        return RateLimitPartition.GetFixedWindowLimiter(
            ClientKey(context, sectionName),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromMinutes(window),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    }

    private static string ClientKey(HttpContext context, string sectionName)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return $"{sectionName}|{ip}";
    }
}
