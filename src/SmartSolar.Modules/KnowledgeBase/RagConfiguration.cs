using System.Text.RegularExpressions;

namespace SmartSolar.Modules.KnowledgeBase;

/// <summary>
/// Admin-managed runtime configuration of the chatbot (generation model and parameters, chunking, retrieval, prompt
/// version). Versions are immutable once activated: DRAFT → ACTIVE → SUPERSEDED, at most one ACTIVE and one DRAFT
/// (partial unique indexes). A rollback creates a new version copied from an older one. Holds no secrets and no URLs;
/// the embedding model and dimension stay fixed by the RAG service environment.
/// </summary>
public sealed class RagConfiguration
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public string Status { get; set; } = null!;
    public string GenerationModel { get; set; } = null!;
    public decimal? Temperature { get; set; }
    public int MaxOutputTokens { get; set; }
    public int ChunkCharacters { get; set; }
    public int ChunkOverlap { get; set; }
    public int TopK { get; set; }
    public decimal MinScore { get; set; }
    public int MaxContextTokens { get; set; }
    public int MaxHistoryMessages { get; set; }
    public string PromptVersion { get; set; } = null!;
    public string? Notes { get; set; }
    /// <summary>Version this one was copied from (null when created from the bootstrap values).</summary>
    public int? BasedOnVersion { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? ActivatedByUserId { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public int Revision { get; set; }

    public RagConfigurationValues Values() => new(GenerationModel, Temperature, MaxOutputTokens, ChunkCharacters, ChunkOverlap, TopK, MinScore, MaxContextTokens,
        MaxHistoryMessages, PromptVersion);

    public void Apply(RagConfigurationValues v)
    {
        GenerationModel = v.GenerationModel;
        Temperature = v.Temperature;
        MaxOutputTokens = v.MaxOutputTokens;
        ChunkCharacters = v.ChunkCharacters;
        ChunkOverlap = v.ChunkOverlap;
        TopK = v.TopK;
        MinScore = v.MinScore;
        MaxContextTokens = v.MaxContextTokens;
        MaxHistoryMessages = v.MaxHistoryMessages;
        PromptVersion = v.PromptVersion;
    }
}

public static class RagConfigurationStatuses
{
    public const string Draft = "DRAFT";
    public const string Active = "ACTIVE";
    public const string Superseded = "SUPERSEDED";
    public static readonly IReadOnlyList<string> All = [Draft, Active, Superseded];
}

public static class RagTokenParameters
{
    public const string MaxTokens = "max_tokens";
    public const string MaxCompletionTokens = "max_completion_tokens";
    public static readonly IReadOnlyList<string> All = [MaxTokens, MaxCompletionTokens];
}

/// <summary>One allowlisted generation model (configuration <c>Rag:GenerationModels</c>).</summary>
public sealed class RagGenerationModelOptions
{
    public string Id { get; set; } = "";
    public string? DisplayName { get; set; }
    public bool SupportsTemperature { get; set; } = true;
    /// <summary><c>max_tokens</c> (classic chat models) or <c>max_completion_tokens</c> (reasoning-style models).</summary>
    public string TokenParameter { get; set; } = RagTokenParameters.MaxTokens;
    public int MaxOutputTokensLimit { get; set; } = 1500;
}

/// <summary>Bounds for admin values (configuration <c>Rag:ConfigurationLimits</c>). Configured bounds can only narrow <see cref="Hard"/>.</summary>
public sealed class RagConfigurationLimits
{
    public int ChunkCharactersMin { get; set; } = 400;
    public int ChunkCharactersMax { get; set; } = 8000;
    public int TopKMin { get; set; } = 1;
    public int TopKMax { get; set; } = 10;
    public decimal MinScoreMin { get; set; } = 0m;
    public decimal MinScoreMax { get; set; } = 1m;
    public int MaxContextTokensMin { get; set; } = 500;
    public int MaxContextTokensMax { get; set; } = 6000;
    public int MaxOutputTokensMin { get; set; } = 64;
    public int MaxOutputTokensMax { get; set; } = 1500;
    public decimal TemperatureMin { get; set; } = 0m;
    public decimal TemperatureMax { get; set; } = 1m;
    public int MaxHistoryMessagesMin { get; set; } = 0;
    public int MaxHistoryMessagesMax { get; set; } = 20;

    /// <summary>Absolute bounds, also enforced by the rag_configuration check constraints.</summary>
    public static readonly RagConfigurationLimits Hard = new();

    /// <summary>Configured bounds intersected with <see cref="Hard"/>; an empty intersection falls back to the hard bound.</summary>
    public RagConfigurationLimits Narrowed()
    {
        var h = Hard;
        (int, int) I(int min, int max, int hMin, int hMax) { var a = Math.Max(min, hMin); var b = Math.Min(max, hMax); return a <= b ? (a, b) : (hMin, hMax); }
        (decimal, decimal) D(decimal min, decimal max, decimal hMin, decimal hMax) { var a = Math.Max(min, hMin); var b = Math.Min(max, hMax); return a <= b ? (a, b) : (hMin, hMax); }
        var r = new RagConfigurationLimits();
        (r.ChunkCharactersMin, r.ChunkCharactersMax) = I(ChunkCharactersMin, ChunkCharactersMax, h.ChunkCharactersMin, h.ChunkCharactersMax);
        (r.TopKMin, r.TopKMax) = I(TopKMin, TopKMax, h.TopKMin, h.TopKMax);
        (r.MinScoreMin, r.MinScoreMax) = D(MinScoreMin, MinScoreMax, h.MinScoreMin, h.MinScoreMax);
        (r.MaxContextTokensMin, r.MaxContextTokensMax) = I(MaxContextTokensMin, MaxContextTokensMax, h.MaxContextTokensMin, h.MaxContextTokensMax);
        (r.MaxOutputTokensMin, r.MaxOutputTokensMax) = I(MaxOutputTokensMin, MaxOutputTokensMax, h.MaxOutputTokensMin, h.MaxOutputTokensMax);
        (r.TemperatureMin, r.TemperatureMax) = D(TemperatureMin, TemperatureMax, h.TemperatureMin, h.TemperatureMax);
        (r.MaxHistoryMessagesMin, r.MaxHistoryMessagesMax) = I(MaxHistoryMessagesMin, MaxHistoryMessagesMax, h.MaxHistoryMessagesMin, h.MaxHistoryMessagesMax);
        return r;
    }
}

/// <summary>The tunable values of one configuration.</summary>
public sealed record RagConfigurationValues(string GenerationModel, decimal? Temperature, int MaxOutputTokens, int ChunkCharacters, int ChunkOverlap, int TopK,
    decimal MinScore, int MaxContextTokens, int MaxHistoryMessages, string PromptVersion)
{
    public bool SameChunking(int? chunkCharacters, int? chunkOverlap) => chunkCharacters == ChunkCharacters && chunkOverlap == ChunkOverlap;
}

/// <summary>What the chatbot and ingestion use right now: the ACTIVE row, or the bootstrap values (version 0) from RagOptions.</summary>
public sealed record EffectiveRagConfiguration(int Version, bool Bootstrap, RagConfigurationValues Values, string TokenParameter);

/// <summary>Model registry, prompt-version allowlist and bounds; validates admin values server-side.</summary>
public sealed partial class RagConfigurationRules
{
    public static readonly IReadOnlyList<RagGenerationModelOptions> DefaultModels =
    [
        new() { Id = "gpt-4.1-mini", DisplayName = "GPT-4.1 mini", SupportsTemperature = true, TokenParameter = RagTokenParameters.MaxTokens, MaxOutputTokensLimit = 1500 },
        new() { Id = "gpt-4.1", DisplayName = "GPT-4.1", SupportsTemperature = true, TokenParameter = RagTokenParameters.MaxTokens, MaxOutputTokensLimit = 1500 },
        new() { Id = "gpt-4o-mini", DisplayName = "GPT-4o mini", SupportsTemperature = true, TokenParameter = RagTokenParameters.MaxTokens, MaxOutputTokensLimit = 1500 },
    ];

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,100}$")]
    private static partial Regex ModelIdPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,20}$")]
    private static partial Regex PromptVersionPattern();

    public RagConfigurationRules(RagOptions options)
    {
        var models = options.GenerationModels is { Count: > 0 } configured ? configured : DefaultModels;
        var problems = new List<string>();
        foreach (var m in models)
        {
            if (!ModelIdPattern().IsMatch(m.Id ?? "")) problems.Add($"model id '{m.Id}' must match ^[A-Za-z0-9._:-]{{1,100}}$");
            if (!RagTokenParameters.All.Contains(m.TokenParameter)) problems.Add($"model '{m.Id}' tokenParameter must be max_tokens or max_completion_tokens");
            if (m.MaxOutputTokensLimit < RagConfigurationLimits.Hard.MaxOutputTokensMin) problems.Add($"model '{m.Id}' maxOutputTokensLimit is too small");
        }
        if (models.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != models.Count) problems.Add("model ids must be unique");
        var prompts = options.PromptVersions is { Count: > 0 } p ? p.Distinct(StringComparer.Ordinal).ToList() : ["v1"];
        if (prompts.Any(v => !PromptVersionPattern().IsMatch(v ?? ""))) problems.Add("prompt versions must match ^[A-Za-z0-9._-]{1,20}$");
        if (problems.Count > 0) throw new InvalidOperationException("Invalid 'Rag:GenerationModels' / 'Rag:PromptVersions': " + string.Join("; ", problems));
        Models = models.ToList();
        PromptVersions = prompts;
        Limits = (options.ConfigurationLimits ?? new RagConfigurationLimits()).Narrowed();
    }

    public IReadOnlyList<RagGenerationModelOptions> Models { get; }
    public IReadOnlyList<string> PromptVersions { get; }
    public RagConfigurationLimits Limits { get; }

    public RagGenerationModelOptions? FindModel(string? id) => Models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));

    /// <summary>Token parameter for a model; a model no longer in the registry keeps the classic parameter.</summary>
    public string TokenParameterFor(string model) => FindModel(model)?.TokenParameter ?? RagTokenParameters.MaxTokens;

    public int MaxOutputTokensFor(RagGenerationModelOptions model) => Math.Min(model.MaxOutputTokensLimit, Limits.MaxOutputTokensMax);

    /// <summary>Field (camelCase) → problems; empty when valid.</summary>
    public Dictionary<string, string[]> Validate(RagConfigurationValues v)
    {
        var errors = new Dictionary<string, string[]>();
        void Add(string field, string message) => errors[field] = errors.TryGetValue(field, out var list) ? [.. list, message] : [message];
        var l = Limits;

        var model = FindModel(v.GenerationModel);
        if (model is null) Add("generationModel", $"Unknown model. Allowed: {string.Join(", ", Models.Select(m => m.Id))}.");
        if (v.Temperature is { } t)
        {
            if (model is { SupportsTemperature: false }) Add("temperature", "This model does not support temperature; leave it empty.");
            else if (t < l.TemperatureMin || t > l.TemperatureMax) Add("temperature", $"Must be between {l.TemperatureMin} and {l.TemperatureMax}.");
            else if (decimal.Round(t, 2) != t) Add("temperature", "At most 2 decimal places.");
        }
        var maxOutput = model is null ? l.MaxOutputTokensMax : MaxOutputTokensFor(model);
        if (v.MaxOutputTokens < l.MaxOutputTokensMin || v.MaxOutputTokens > maxOutput) Add("maxOutputTokens", $"Must be between {l.MaxOutputTokensMin} and {maxOutput}.");
        if (v.ChunkCharacters < l.ChunkCharactersMin || v.ChunkCharacters > l.ChunkCharactersMax)
            Add("chunkCharacters", $"Must be between {l.ChunkCharactersMin} and {l.ChunkCharactersMax}.");
        if (v.ChunkOverlap < 0 || v.ChunkOverlap > v.ChunkCharacters / 2 || v.ChunkOverlap >= v.ChunkCharacters)
            Add("chunkOverlap", "Must be between 0 and half of chunkCharacters (and smaller than chunkCharacters).");
        if (v.TopK < l.TopKMin || v.TopK > l.TopKMax) Add("topK", $"Must be between {l.TopKMin} and {l.TopKMax}.");
        if (v.MinScore < l.MinScoreMin || v.MinScore > l.MinScoreMax) Add("minScore", $"Must be between {l.MinScoreMin} and {l.MinScoreMax}.");
        else if (decimal.Round(v.MinScore, 3) != v.MinScore) Add("minScore", "At most 3 decimal places.");
        if (v.MaxContextTokens < l.MaxContextTokensMin || v.MaxContextTokens > l.MaxContextTokensMax)
            Add("maxContextTokens", $"Must be between {l.MaxContextTokensMin} and {l.MaxContextTokensMax}.");
        if (v.MaxHistoryMessages < l.MaxHistoryMessagesMin || v.MaxHistoryMessages > l.MaxHistoryMessagesMax)
            Add("maxHistoryMessages", $"Must be between {l.MaxHistoryMessagesMin} and {l.MaxHistoryMessagesMax}.");
        if (!PromptVersions.Contains(v.PromptVersion ?? "", StringComparer.Ordinal)) Add("promptVersion", $"Unknown prompt version. Allowed: {string.Join(", ", PromptVersions)}.");
        return errors;
    }

    /// <summary>The bootstrap configuration (version 0) from RagOptions.</summary>
    public EffectiveRagConfiguration Bootstrap(RagOptions options)
    {
        var model = FindModel(options.GenerationModel);
        var values = new RagConfigurationValues(options.GenerationModel, model is { SupportsTemperature: false } ? null : options.Temperature, options.MaxOutputTokens,
            options.ChunkCharacters, options.ChunkOverlap, options.TopK, options.MinScore, options.MaxContextTokens, options.MaxHistoryMessages, options.PromptVersion);
        return new EffectiveRagConfiguration(0, true, values, TokenParameterFor(options.GenerationModel));
    }

    public EffectiveRagConfiguration Effective(RagConfiguration active) => new(active.Version, false, active.Values(), TokenParameterFor(active.GenerationModel));
}

/// <summary>Reads the effective configuration (per request, cached briefly).</summary>
public interface IRagConfigurationProvider
{
    Task<EffectiveRagConfiguration> GetAsync(CancellationToken cancellationToken);

    /// <summary>Drops the cached value on this instance (called after an activation or rollback).</summary>
    void Invalidate();
}

/// <summary>
/// Process-wide cache of the effective configuration. Other instances pick up an activation within the TTL (≤ 15 s).
/// A generation counter stops a read that started before an invalidation from caching the old value.
/// </summary>
public sealed class RagConfigurationCache(TimeSpan ttl)
{
    private readonly object _gate = new();
    private EffectiveRagConfiguration? _value;
    private DateTimeOffset _expiresAt;
    private long _generation;

    public TimeSpan Ttl { get; } = ttl < TimeSpan.Zero ? TimeSpan.Zero : ttl > TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : ttl;

    public static RagConfigurationCache From(RagOptions options) => new(TimeSpan.FromSeconds(options.ConfigurationCacheSeconds));

    public bool TryGet(DateTimeOffset now, out EffectiveRagConfiguration? value, out long generation)
    {
        lock (_gate)
        {
            generation = _generation;
            value = _value is not null && now < _expiresAt ? _value : null;
            return value is not null;
        }
    }

    public void Set(EffectiveRagConfiguration value, DateTimeOffset now, long generation)
    {
        if (Ttl == TimeSpan.Zero) return;
        lock (_gate)
        {
            if (generation != _generation) return;
            _value = value;
            _expiresAt = now + Ttl;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _value = null;
        }
    }
}

public sealed class EffectiveRagConfigurationProvider(IKnowledgeUnitOfWork unitOfWork, RagOptions options, RagConfigurationRules rules, RagConfigurationCache cache,
    TimeProvider time) : IRagConfigurationProvider
{
    public async Task<EffectiveRagConfiguration> GetAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGet(time.GetUtcNow(), out var cached, out var generation)) return cached!;
        var active = await unitOfWork.FindConfigurationAsync(RagConfigurationStatuses.Active, false, cancellationToken);
        var value = active is null ? rules.Bootstrap(options) : rules.Effective(active);
        cache.Set(value, time.GetUtcNow(), generation);
        return value;
    }

    public void Invalidate() => cache.Invalidate();

    /// <summary>Reads the ACTIVE row on every call (no cache): for services composed without dependency injection.</summary>
    public static EffectiveRagConfigurationProvider Uncached(IKnowledgeUnitOfWork unitOfWork, RagOptions options, TimeProvider time)
        => new(unitOfWork, options, new RagConfigurationRules(options), new RagConfigurationCache(TimeSpan.Zero), time);
}
