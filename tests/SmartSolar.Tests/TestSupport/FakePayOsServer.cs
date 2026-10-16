using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.ExternalProviders.PayOS;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// SYNTHETIC PayOS merchant API behind an HttpMessageHandler: creates links, answers status queries
/// from a programmable table and cancels. The real <see cref="PayOsGateway"/> talks to it, so request
/// signing and parsing are exercised without any network call or real credential.
/// </summary>
public sealed class FakePayOsServer : HttpMessageHandler
{
    public const string ChecksumKey = "synthetic-checksum-key-for-tests";
    public ConcurrentDictionary<long, (string Status, long AmountPaid, string? Reference)> Links { get; } = new();
    public ConcurrentQueue<(string Method, string Path, string? Body, string? ClientId)> Requests { get; } = new();
    public bool Down { get; set; }

    public PayOsOptions Options { get; } = new() { Enabled = true, ClientId = "client", ApiKey = "api-key", ChecksumKey = ChecksumKey, BaseUrl = "https://payos.test" };

    public PayOsGateway Gateway() => new(new HttpClient(this) { BaseAddress = new Uri("https://payos.test/") }, Options, NullLogger<PayOsGateway>.Instance);

    public int CreateCalls => Requests.Count(r => r.Method == "POST" && r.Path == "/v2/payment-requests");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue((request.Method.Method, request.RequestUri!.AbsolutePath, body, request.Headers.TryGetValues("x-client-id", out var v) ? v.First() : null));
        if (Down) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        var path = request.RequestUri.AbsolutePath;
        if (request.Method == HttpMethod.Post && path == "/v2/payment-requests")
        {
            using var doc = JsonDocument.Parse(body!);
            var orderCode = doc.RootElement.GetProperty("orderCode").GetInt64();
            Links[orderCode] = ("PENDING", 0, null);
            return Json(new { code = "00", desc = "success", data = new { checkoutUrl = $"https://pay.payos.test/{orderCode}", paymentLinkId = $"link-{orderCode}", qrCode = "000201", status = "PENDING" } });
        }
        if (request.Method == HttpMethod.Get && path.StartsWith("/v2/payment-requests/", StringComparison.Ordinal))
        {
            var orderCode = long.Parse(path.Split('/').Last());
            if (!Links.TryGetValue(orderCode, out var link)) return Json(new { code = "101", desc = "not found", data = (object?)null });
            return Json(new
            {
                code = "00", desc = "success",
                data = new { orderCode, amount = link.AmountPaid, amountPaid = link.AmountPaid, status = link.Status,
                    transactions = link.Reference is null ? Array.Empty<object>() : new object[] { new { reference = link.Reference, amount = link.AmountPaid, transactionDateTime = "2026-10-20 09:00:00" } } },
            });
        }
        if (request.Method == HttpMethod.Post && path.EndsWith("/cancel", StringComparison.Ordinal))
        {
            var orderCode = long.Parse(path.Split('/')[^2]);
            if (Links.TryGetValue(orderCode, out var link)) Links[orderCode] = ("CANCELLED", link.AmountPaid, link.Reference);
            return Json(new { code = "00", desc = "success", data = new { status = "CANCELLED" } });
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    /// <summary>Builds a webhook body signed exactly like PayOS does (sorted data keys, HMAC-SHA256 hex).</summary>
    public string Webhook(long orderCode, long amount, string reference, string code = "00", bool success = true, string? signatureOverride = null)
    {
        var data = new Dictionary<string, object?>
        {
            ["orderCode"] = orderCode, ["amount"] = amount, ["description"] = $"SS{orderCode % 10_000_000:D7}", ["accountNumber"] = "12345678",
            ["reference"] = reference, ["transactionDateTime"] = "2026-10-20 09:00:00", ["currency"] = "VND", ["paymentLinkId"] = $"link-{orderCode}",
            ["code"] = code, ["desc"] = "success", ["counterAccountBankId"] = "", ["counterAccountBankName"] = "", ["counterAccountName"] = null,
            ["counterAccountNumber"] = null, ["virtualAccountName"] = null, ["virtualAccountNumber"] = "",
        };
        var dataJson = JsonSerializer.Serialize(data);
        using var doc = JsonDocument.Parse(dataJson);
        var signature = signatureOverride ?? Gateway().Sign(PayOsGateway.DataToSign(doc.RootElement));
        return $"{{\"code\":\"00\",\"desc\":\"success\",\"success\":{(success ? "true" : "false")},\"data\":{dataJson},\"signature\":\"{signature}\"}}";
    }

    private static HttpResponseMessage Json(object value)
        => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
}
