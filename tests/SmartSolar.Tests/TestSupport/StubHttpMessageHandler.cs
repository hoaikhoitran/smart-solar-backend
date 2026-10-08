using System.Net;
using System.Text;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// Primary HTTP handler for provider tests: replays queued responses and records every
/// request. No network access happens in tests that use it.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();
    private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _fallback;

    public List<HttpRequestMessage> Requests { get; } = new();

    public StubHttpMessageHandler Enqueue(HttpStatusCode status, string body)
    {
        _responses.Enqueue((_, _) => Task.FromResult(Json(status, body)));
        return this;
    }

    public StubHttpMessageHandler Always(HttpStatusCode status, string body)
    {
        _fallback = (_, _) => Task.FromResult(Json(status, body));
        return this;
    }

    /// <summary>Every request waits (honoring cancellation) longer than any test timeout.</summary>
    public StubHttpMessageHandler AlwaysHang()
    {
        _fallback = async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return Json(HttpStatusCode.OK, "{}");
        };
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var responder = _responses.Count > 0 ? _responses.Dequeue() : _fallback
            ?? throw new InvalidOperationException("No stubbed response left.");
        return responder(request, cancellationToken);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
