using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using SmartSolar.Modules.Identity.Contracts.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using SmartSolar.Modules.Identity.Options;

namespace SmartSolar.Api.Extensions;

public static class JwtAuthenticationExtensions
{
    /// <summary>
    /// Validates bearer tokens with the same issuer, audience and key used to
    /// issue them. A missing or too-short signing key fails startup.
    /// </summary>
    public static IServiceCollection AddJwtAuthentication(
        this IServiceCollection services,
        JwtOptions jwtOptions)
    {
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwtOptions.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwtOptions.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };

                // A valid signature is not enough: the account must still be ACTIVE, not deleted,
                // and on the same authorization version the token was issued with. Status changes,
                // role changes and soft deletes bump that version, so stale tokens stop working at once.
                options.Events = new JwtBearerEvents
                {
                    // Browsers cannot set the Authorization header on a WebSocket: the SignalR client sends the access token as the
                    // access_token query parameter. Accepted ONLY on the realtime hub path (never on REST endpoints); the same validation
                    // and account re-check below apply. The query string is not logged (Microsoft.AspNetCore logs at Warning).
                    OnMessageReceived = context =>
                    {
                        if (string.IsNullOrEmpty(context.Token)
                            && context.HttpContext.Request.Path.StartsWithSegments(SmartSolar.Api.Realtime.RealtimeHub.Path)
                            && context.Request.Query.TryGetValue("access_token", out var token) && !string.IsNullOrEmpty(token))
                            context.Token = token;
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = async context =>
                    {
                        var principal = context.Principal;
                        var subject = principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                            ?? principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                        var versionText = principal?.FindFirst(AuthVersionClaim.Type)?.Value;
                        var version = 0;

                        if (!Guid.TryParse(subject, out var userId)
                            || (versionText is not null && !int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out version)))
                        {
                            context.Fail("The access token is not bound to a valid account.");
                            return;
                        }

                        var check = context.HttpContext.RequestServices.GetRequiredService<IAccessTokenRevocationCheck>();
                        if (!await check.IsCurrentAsync(userId, version, context.HttpContext.RequestAborted))
                        {
                            context.Fail("The access token is no longer valid for this account.");
                        }
                    }
                };
            });

        services.AddAuthorization();

        return services;
    }
}
