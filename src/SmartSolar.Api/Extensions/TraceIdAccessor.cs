using System.Diagnostics;

namespace SmartSolar.Api.Extensions;

public static class TraceIdAccessor
{
    /// <summary>
    /// The current request's trace identifier: the W3C activity id when tracing
    /// is active, otherwise ASP.NET Core's own request identifier.
    /// </summary>
    public static string GetTraceId(this HttpContext httpContext)
        => Activity.Current?.Id ?? httpContext.TraceIdentifier;
}
