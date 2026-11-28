using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace SmartSolar.Api.Realtime;

/// <summary>
/// Realtime notification hub (2026-11-28), <c>/hubs/realtime</c>, only when <c>Realtime:Enabled</c>. Authenticated with the same JWT bearer scheme
/// and account re-check as the REST API. It exposes no client-invokable method and no groups: the server sends <c>realtime</c> messages to
/// <c>Clients.User(id)</c>, where the id is the authenticated subject and the recipient is the one the business code persisted.
/// </summary>
[Authorize]
public sealed class RealtimeHub : Hub
{
    public const string Path = "/hubs/realtime";

    /// <summary>Client method name of every server → client message (payload: <see cref="SmartSolar.Modules.Common.Realtime.RealtimeEnvelope"/>).</summary>
    public const string ClientMethod = "realtime";
}

/// <summary>Same identity as <c>OperationResultControllerBase.CurrentUserId</c>: the <c>sub</c> (or NameIdentifier) claim, as a GUID "D" string.</summary>
public sealed class SubjectUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => From(connection.User);

    public static string? From(ClaimsPrincipal? user)
        => Guid.TryParse(user?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id)
            ? id.ToString("D")
            : null;
}
