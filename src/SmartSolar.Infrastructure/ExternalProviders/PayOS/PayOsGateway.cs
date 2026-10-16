using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Payments;

namespace SmartSolar.Infrastructure.ExternalProviders.PayOS;

/// <summary>Configuration section "Providers:PayOS". Credentials only from the environment, never logged or stored in the database.</summary>
public sealed class PayOsOptions
{
    public const string SectionName = "Providers:PayOS";
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://api-merchant.payos.vn";
    public string? ClientId { get; set; }
    public string? ApiKey { get; set; }
    public string? ChecksumKey { get; set; }
    public int TimeoutSeconds { get; set; } = 15;

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ChecksumKey);
}

/// <summary>
/// PayOS merchant API v2 (https://payos.vn/docs/api/): create payment link, get payment info, cancel,
/// and webhook verification. Signatures are HMAC-SHA256 (hex) with the checksum key:
/// create-link over "amount=…&amp;cancelUrl=…&amp;description=…&amp;orderCode=…&amp;returnUrl=…"; webhook over the
/// data object's keys sorted alphabetically as "key=value&amp;…" (null → empty, arrays JSON-encoded).
/// Request URLs never contain credentials; credentials travel only in the x-client-id / x-api-key headers.
/// </summary>
public sealed class PayOsGateway(HttpClient http, PayOsOptions options, ILogger<PayOsGateway> logger) : IPaymentGateway
{
    private static readonly TimeZoneInfo Vietnam = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    public string ProviderName => "PAYOS";

    public async Task<PaymentLinkResult> CreatePaymentLinkAsync(PaymentLinkRequest request, CancellationToken cancellationToken)
    {
        if (!options.IsConfigured) return new PaymentLinkResult(GatewayCallStatus.NotConfigured, null, null, null, "PayOS is not configured.");
        var signature = Sign($"amount={request.Amount}&cancelUrl={request.CancelUrl}&description={request.Description}&orderCode={request.OrderCode}&returnUrl={request.ReturnUrl}");
        var body = new Dictionary<string, object?>
        {
            ["orderCode"] = request.OrderCode, ["amount"] = request.Amount, ["description"] = request.Description,
            ["returnUrl"] = request.ReturnUrl, ["cancelUrl"] = request.CancelUrl, ["expiredAt"] = request.ExpiresAt.ToUnixTimeSeconds(),
            ["buyerName"] = request.BuyerName, ["buyerEmail"] = request.BuyerEmail, ["buyerPhone"] = request.BuyerPhone, ["signature"] = signature,
        };
        var (ok, data, error) = await SendAsync(HttpMethod.Post, "v2/payment-requests", body, cancellationToken);
        if (!ok) return new PaymentLinkResult(error == "unavailable" ? GatewayCallStatus.Unavailable : GatewayCallStatus.Rejected, null, null, null, error);
        return new PaymentLinkResult(GatewayCallStatus.Succeeded, Str(data, "paymentLinkId"), Str(data, "checkoutUrl"), Str(data, "qrCode"), null);
    }

    public async Task<ProviderPaymentStatus> GetPaymentStatusAsync(long orderCode, CancellationToken cancellationToken)
    {
        if (!options.IsConfigured) return new ProviderPaymentStatus(GatewayCallStatus.NotConfigured, null, null, null, null, null, "PayOS is not configured.");
        var (ok, data, error) = await SendAsync(HttpMethod.Get, $"v2/payment-requests/{orderCode}", null, cancellationToken);
        if (!ok) return new ProviderPaymentStatus(error == "unavailable" ? GatewayCallStatus.Unavailable : GatewayCallStatus.Rejected, null, null, null, null, null, error);

        string? reference = null;
        DateTimeOffset? at = null;
        if (data.TryGetProperty("transactions", out var transactions) && transactions.ValueKind == JsonValueKind.Array && transactions.GetArrayLength() > 0)
        {
            var last = transactions[transactions.GetArrayLength() - 1];
            reference = Str(last, "reference");
            at = ParseLocal(Str(last, "transactionDateTime"));
        }
        return new ProviderPaymentStatus(GatewayCallStatus.Succeeded, Str(data, "status"), Long(data, "amount"), Long(data, "amountPaid"), reference, at, null);
    }

    public async Task<GatewayCallStatus> CancelPaymentLinkAsync(long orderCode, string reason, CancellationToken cancellationToken)
    {
        if (!options.IsConfigured) return GatewayCallStatus.NotConfigured;
        var (ok, _, error) = await SendAsync(HttpMethod.Post, $"v2/payment-requests/{orderCode}/cancel", new { cancellationReason = reason }, cancellationToken);
        return ok ? GatewayCallStatus.Succeeded : error == "unavailable" ? GatewayCallStatus.Unavailable : GatewayCallStatus.Rejected;
    }

    public WebhookNotification VerifyWebhook(string rawBody)
    {
        static WebhookNotification Invalid(string error) => new(false, false, null, null, null, null, null, null, null, error);
        if (string.IsNullOrWhiteSpace(options.ChecksumKey)) return Invalid("not configured");
        try
        {
            using var document = JsonDocument.Parse(rawBody, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("signature", out var signatureElement) || signatureElement.ValueKind != JsonValueKind.String)
                return Invalid("malformed");

            var expected = Sign(DataToSign(data));
            var provided = signatureElement.GetString() ?? "";
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(provided.ToLowerInvariant())))
                return Invalid("signature mismatch");

            var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
            return new WebhookNotification(true, success, Str(data, "code"), Long(data, "orderCode"), Long(data, "amount"), Str(data, "reference"),
                Str(data, "currency"), ParseLocal(Str(data, "transactionDateTime")), Str(data, "paymentLinkId"), null);
        }
        catch (JsonException)
        {
            return Invalid("malformed");
        }
    }

    /// <summary>Sorted "key=value&amp;…" exactly as PayOS's reference implementation builds it.</summary>
    public static string DataToSign(JsonElement data)
        => string.Join('&', data.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => $"{p.Name}={ValueText(p.Value)}"));

    private static string ValueText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.String => value.GetString() is "null" or "undefined" ? "" : value.GetString()!,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => "[" + string.Join(',', value.EnumerateArray().Select(SortedJson)) + "]",
        _ => value.GetRawText(),
    };

    private static string SortedJson(JsonElement element) => element.ValueKind == JsonValueKind.Object
        ? "{" + string.Join(',', element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => $"{JsonSerializer.Serialize(p.Name)}:{SortedJson(p.Value)}")) + "}"
        : element.GetRawText();

    public string Sign(string data)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.ChecksumKey ?? ""), Encoding.UTF8.GetBytes(data))).ToLowerInvariant();

    private async Task<(bool Ok, JsonElement Data, string? Error)> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("x-client-id", options.ClientId);
        request.Headers.Add("x-api-key", options.ApiKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if ((int)response.StatusCode >= 500) return (false, default, "unavailable");
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var code = root.TryGetProperty("code", out var c) ? c.GetString() : null;
            if (code != "00" || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                logger.LogWarning("PayOS rejected a {Method} request with code {Code}.", method, code);
                return (false, default, $"provider code {code}");
            }
            return (true, data.Clone(), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("PayOS request failed ({ExceptionType}).", ex.GetType().Name);
            return (false, default, "unavailable");
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null } : null;

    private static long? Long(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    private static DateTimeOffset? ParseLocal(string? value)
    {
        if (value is null) return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset) && value.Contains('+')) return withOffset.ToUniversalTime();
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? new DateTimeOffset(local, Vietnam.GetUtcOffset(local)).ToUniversalTime()
            : null;
    }
}
