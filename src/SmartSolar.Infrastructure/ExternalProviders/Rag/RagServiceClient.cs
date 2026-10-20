using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartSolar.Modules.KnowledgeBase;

namespace SmartSolar.Infrastructure.ExternalProviders.Rag;

/// <summary>
/// HTTP client of the internal FastAPI RAG service. Every request is signed:
/// <c>X-SmartSolar-Timestamp</c> (unix seconds) and <c>X-SmartSolar-Signature</c> =
/// hex(HMAC-SHA256(secret, timestamp + "." + body)). The service rejects stale timestamps (replay)
/// and bad signatures. No user identity is forwarded: the API already decided what may be read.
/// Requests carry the effective admin configuration (ingest: config version + chunk settings; answer: config version, model,
/// temperature — JSON null means "omit" —, token parameter, output budget, min score, prompt version; in bootstrap the model,
/// temperature and token parameter are left out so the service's environment defaults apply). Compatibility: the current
/// FastAPI service forbids unknown fields (422), so deploy the RAG service before (or together with) an API that sends new
/// fields. A pre-configuration service (plain pydantic models, unknown fields ignored) accepts these requests but ignores the
/// settings and echoes nothing; since only echoed chunk settings are recorded as indexed, every version it indexes then looks
/// stale until the service is upgraded and the versions are reindexed.
/// </summary>
public sealed class RagServiceClient(HttpClient http, RagOptions options, TimeProvider time, ILogger<RagServiceClient> logger) : IRagServiceClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<RagIngestResult> IngestAsync(RagIngestRequest request, CancellationToken cancellationToken)
    {
        var (ok, body, error) = await PostAsync("internal/v1/ingest", request, options.IngestTimeoutSeconds, cancellationToken);
        if (!ok || body is not { } b) return new RagIngestResult(false, "FAILED", null, null, null, null, null, error);
        return new RagIngestResult(true, Str(b, "status") ?? "FAILED", Int(b, "pageCount"), Int(b, "chunkCount"), Int(b, "characters"), Str(b, "embeddingModel"),
            Int(b, "embeddingDimension"), Str(b, "error"), Int(b, "configVersion"), Int(b, "chunkCharacters"), Int(b, "chunkOverlap"));
    }

    public async Task<bool> DeleteVectorsAsync(Guid versionId, CancellationToken cancellationToken)
        => (await PostAsync("internal/v1/delete", new { versionId }, options.RequestTimeoutSeconds, cancellationToken)).Ok;

    public async Task<RagAnswerResult> AnswerAsync(RagAnswerRequest request, CancellationToken cancellationToken)
    {
        var (ok, body, error) = await PostAsync("internal/v1/answer", AnswerPayload(request), options.RequestTimeoutSeconds, cancellationToken);
        if (!ok || body is not { } b) return new RagAnswerResult(false, error, null, false, [], 0, 0, null, 0, 0);
        var citations = b.TryGetProperty("citations", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(c => new RagCitation(Guid.Parse(Str(c, "documentId")!), Guid.Parse(Str(c, "versionId")!), Str(c, "chunkId") ?? "",
                Int(c, "page"), Str(c, "section"), c.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0, Str(c, "snippet"))).ToList()
            : [];
        var usage = b.TryGetProperty("usage", out var u) ? u : default;
        return new RagAnswerResult(true, null, Str(b, "answer"), b.TryGetProperty("grounded", out var g) && g.ValueKind == JsonValueKind.True, citations,
            usage.ValueKind == JsonValueKind.Object ? Int(usage, "promptTokens") ?? 0 : 0, usage.ValueKind == JsonValueKind.Object ? Int(usage, "completionTokens") ?? 0 : 0,
            Str(b, "model"), Int(b, "retrievalMs") ?? 0, Int(b, "generationMs") ?? 0, Int(b, "configVersion"));
    }

    /// <summary>
    /// The answer body. With a generation model (ACTIVE admin configuration) every field is sent, a null temperature as JSON null
    /// ("omit temperature"). Without one (bootstrap) generationModel, temperature and tokenParameter are left out entirely, so the
    /// RAG service applies its environment defaults: an absent temperature means RAG_LLM_TEMPERATURE, an explicit null would not.
    /// </summary>
    public static System.Text.Json.Nodes.JsonObject AnswerPayload(RagAnswerRequest request)
    {
        var node = JsonSerializer.SerializeToNode(request, Json)!.AsObject();
        if (request.GenerationModel is null)
        {
            node.Remove("generationModel");
            node.Remove("temperature");
            node.Remove("tokenParameter");
        }
        return node;
    }

    public static string Sign(string secret, string timestamp, string body)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();

    /// <summary>
    /// One signed call with its own deadline. Our timeout and transport failures come back as an unreached
    /// result ("timeout" / "unavailable") for the caller to retry; cancellation by the caller (service
    /// shutdown, client disconnect) is propagated as <see cref="OperationCanceledException"/>.
    /// </summary>
    private async Task<(bool Ok, JsonElement? Body, string Error)> PostAsync(string path, object payload, int timeoutSeconds, CancellationToken cancellationToken)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ServiceSharedSecret)) return (false, null, "RAG service not configured");
        var json = JsonSerializer.Serialize(payload, Json);
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-SmartSolar-Timestamp", timestamp);
        request.Headers.Add("X-SmartSolar-Signature", Sign(options.ServiceSharedSecret, timestamp, json));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 1800)));
        try
        {
            using var response = await http.SendAsync(request, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("RAG service returned HTTP {Status} for {Path}.", (int)response.StatusCode, path);
                return (false, null, $"RAG service returned HTTP {(int)response.StatusCode}");
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            return (true, document.RootElement.Clone(), "");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our deadline (or HttpClient.Timeout) fired, not the caller.
            logger.LogWarning("RAG service call to {Path} timed out after {Seconds}s.", path, timeoutSeconds);
            return (false, null, "timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogWarning("RAG service call to {Path} failed ({ExceptionType}).", path, ex.GetType().Name);
            return (false, null, "RAG service unavailable");
        }
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
