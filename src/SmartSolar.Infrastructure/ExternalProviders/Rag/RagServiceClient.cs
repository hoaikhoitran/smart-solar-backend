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
        var (ok, body, error, billing, _) = await PostAsync("internal/v1/ingest", request, options.IngestTimeoutSeconds, cancellationToken);
        if (!ok || body is not { } b) return new RagIngestResult(false, "FAILED", null, null, null, null, null, error, Billing: billing);
        return new RagIngestResult(true, Str(b, "status") ?? "FAILED", Int(b, "pageCount"), Int(b, "chunkCount"), Int(b, "characters"), Str(b, "embeddingModel"),
            Int(b, "embeddingDimension"), Str(b, "error"), Int(b, "configVersion"), Int(b, "chunkCharacters"), Int(b, "chunkOverlap"), Long(b, "embeddingTokens"),
            Str(b, "embeddingProvider"), Str(b, "billing") ?? "none");
    }

    public async Task<bool> DeleteVectorsAsync(Guid versionId, CancellationToken cancellationToken)
        => (await PostAsync("internal/v1/delete", new { versionId }, options.RequestTimeoutSeconds, cancellationToken)).Ok;

    public async Task<RagAnswerResult> AnswerAsync(RagAnswerRequest request, CancellationToken cancellationToken)
    {
        // The service is told how long we wait (one second less, for the transfer back), so it neither queues nor generates an answer
        // we will no longer read (an abandoned request would otherwise hold the generation slot until the provider timeout).
        // The admin generation timeout of the active configuration, never longer than Rag:RequestTimeoutSeconds.
        var timeoutSeconds = Math.Clamp(Math.Min(request.TimeoutSeconds ?? options.RequestTimeoutSeconds, options.RequestTimeoutSeconds), 5, 120);
        var payload = AnswerPayload(request);
        payload["deadlineSeconds"] = timeoutSeconds - 1;
        var (ok, body, error, billing, failure) = await PostAsync("internal/v1/answer", payload, timeoutSeconds, cancellationToken);
        if (!ok || body is not { } b)
        {
            // A failed answer still reports what was spent before the failure (the question embedding) and whether the provider may have
            // charged the failed call; without a body (our timeout, a transport error) the charge is uncertain.
            var spent = failure is { } f && f.TryGetProperty("usage", out var fu) && fu.ValueKind == JsonValueKind.Object ? fu : default;
            return new RagAnswerResult(false, error, null, false, [], 0, 0, null, 0, 0,
                EmbeddingTokens: spent.ValueKind == JsonValueKind.Object ? Long(spent, "embeddingTokens") ?? 0 : 0,
                EmbeddingProvider: spent.ValueKind == JsonValueKind.Object ? Str(spent, "embeddingProvider") : null, Billing: billing);
        }
        var citations = b.TryGetProperty("citations", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(c => new RagCitation(Guid.Parse(Str(c, "documentId")!), Guid.Parse(Str(c, "versionId")!), Str(c, "chunkId") ?? "",
                Int(c, "page"), Str(c, "section"), c.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0, Str(c, "snippet"))).ToList()
            : [];
        var usage = b.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
        var hasUsage = usage.ValueKind == JsonValueKind.Object;
        return new RagAnswerResult(true, null, Str(b, "answer"), b.TryGetProperty("grounded", out var g) && g.ValueKind == JsonValueKind.True, citations,
            hasUsage ? Int(usage, "promptTokens") ?? 0 : 0, hasUsage ? Int(usage, "completionTokens") ?? 0 : 0,
            Str(b, "model"), Int(b, "retrievalMs") ?? 0, Int(b, "generationMs") ?? 0, Int(b, "configVersion"),
            EmbeddingTokens: hasUsage ? Long(usage, "embeddingTokens") ?? 0 : 0, EmbeddingModel: hasUsage ? Str(usage, "embeddingModel") : null,
            EmbeddingProvider: hasUsage ? Str(usage, "embeddingProvider") : null,
            // An older service reports no providers: a generation it ran is then costed as the configured (billed) model.
            GenerationProvider: hasUsage && usage.TryGetProperty("generationProvider", out var gp) ? (gp.ValueKind == JsonValueKind.String ? gp.GetString() : null)
                : (Int(usage, "completionTokens") ?? 0) > 0 ? RagProviders.OpenAi : null,
            CachedPromptTokens: hasUsage ? Int(usage, "cachedPromptTokens") ?? 0 : 0, ReasoningTokens: hasUsage ? Int(usage, "reasoningTokens") ?? 0 : 0,
            UsageReported: !hasUsage || !usage.TryGetProperty("usageReported", out var ur) || ur.ValueKind != JsonValueKind.False,
            ContextMs: Int(b, "contextMs") ?? 0,
            // Safe diagnostics (no content); absent from older services.
            InsufficientReason: Str(b, "insufficientReason"), RetrievedChunks: Int(b, "retrievedChunks"), UsableChunks: Int(b, "usableChunks"));
    }

    /// <summary>
    /// The answer body. With a generation model (ACTIVE admin configuration) every field is sent, a null temperature as JSON null
    /// ("omit temperature"). Without one (bootstrap) generationModel, temperature and tokenParameter are left out entirely, so the
    /// RAG service applies its environment defaults: an absent temperature means RAG_LLM_TEMPERATURE, an explicit null would not.
    /// </summary>
    public static System.Text.Json.Nodes.JsonObject AnswerPayload(RagAnswerRequest request)
    {
        var node = JsonSerializer.SerializeToNode(request, Json)!.AsObject();
        // Client-side only (the call's own deadline); the service forbids unknown fields.
        node.Remove("timeoutSeconds");
        if (request.GenerationModel is null)
        {
            node.Remove("generationModel");
            node.Remove("temperature");
            node.Remove("tokenParameter");
        }
        // Sent only when set, so an API talking to a RAG service without the field (older build) keeps working for models without it.
        if (request.ReasoningEffort is null) node.Remove("reasoningEffort");
        return node;
    }

    public static string Sign(string secret, string timestamp, string body)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();

    /// <summary>
    /// One signed call with its own deadline. Our timeout and transport failures come back as an unreached
    /// result ("timeout" / "unavailable") for the caller to retry; cancellation by the caller (service
    /// shutdown, client disconnect) is propagated as <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <returns>
    /// <c>Billing</c>: "none" when the call certainly cost nothing (not configured, or the service said so), "uncertain" when it may have
    /// reached a paid provider (our timeout, a transport error, a failure body without a billing statement). <c>Failure</c>: a 503 body.
    /// </returns>
    private async Task<(bool Ok, JsonElement? Body, string Error, string Billing, JsonElement? Failure)> PostAsync(string path, object payload, int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ServiceSharedSecret)) return (false, null, "RAG service not configured", "none", null);
        // Every call carries its own requestId inside the signed body. The service accepts a signature once (replay protection); without
        // a unique field two legitimate calls with identical content in the same second (two users asking the same question — common now
        // that every role sends the same allow-list) had the same signature and the second was refused as "replayed" (503 to the user).
        var node = JsonSerializer.SerializeToNode(payload, Json)!.AsObject();
        node["requestId"] = Guid.NewGuid().ToString("N");
        var json = node.ToJsonString(Json);
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
                // The service's 503 says why ("busy": every generation slot taken; "deadline exceeded": it would not finish in time; "provider
                // rate limited"), whether the provider may have charged the failed call, and what was spent before the failure.
                var failure = (int)response.StatusCode == 503 ? await FailureAsync(response, deadline.Token) : null;
                var reason = failure is { } f && Str(f, "detail") is { } d ? d : null;
                // Busy and refused requests never reached a provider; a failure body without a billing statement (older service) may have.
                var billing = failure is { } fb && Str(fb, "billing") is { } bs ? bs : reason == "busy" ? "none" : (int)response.StatusCode >= 500 ? "uncertain" : "none";
                logger.LogWarning("RAG service returned HTTP {Status} ({Reason}, billing {Billing}) for {Path}.", (int)response.StatusCode, reason ?? "-", billing, path);
                return reason switch
                {
                    "busy" => (false, null, "busy", "none", failure),
                    "deadline exceeded" => (false, null, "timeout", billing, failure),
                    "provider rate limited" => (false, null, "provider rate limited", billing, failure),
                    _ => (false, null, $"RAG service returned HTTP {(int)response.StatusCode}", billing, failure),
                };
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            return (true, document.RootElement.Clone(), "", "none", null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our deadline (or HttpClient.Timeout) fired, not the caller. The service may still have called (and paid) the provider.
            logger.LogWarning("RAG service call to {Path} timed out after {Seconds}s.", path, timeoutSeconds);
            return (false, null, "timeout", "uncertain", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogWarning("RAG service call to {Path} failed ({ExceptionType}).", path, ex.GetType().Name);
            return (false, null, "RAG service unavailable", "uncertain", null);
        }
    }

    /// <summary>FastAPI's error body (<c>{"detail": "…", "billing": "…", "usage": {…}}</c>), read with a size cap; null when absent or not JSON.</summary>
    private static async Task<JsonElement?> FailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (text.Length > 4096) return null;
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A missing block (e.g. no "usage" from an older service) is a default JsonElement: TryGetProperty would throw on it, so it reads as absent.
    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    private static long? Long(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;
}
