using System.Text.RegularExpressions;

namespace SmartSolar.Modules.KnowledgeBase;

/// <summary>
/// Admin-managed runtime configuration of the chatbot (generation model and parameters, chunking, retrieval, prompt
/// version). Versions are immutable once activated: DRAFT → ACTIVE → SUPERSEDED, at most one ACTIVE and one DRAFT
/// (partial unique indexes). A rollback creates a new version copied from an older one. Holds no secrets and no URLs;
/// the embedding model and dimension stay fixed by the RAG service environment.
/// <para>
/// Since 2026-11-17 a version also carries the cost controls (reasoning effort, the global generation switch, per-user quotas,
/// concurrency ceilings, the question length and the USD budgets), so a budget change is versioned, validated against hard
/// ceilings, audited (who / when) and can be rolled back exactly like any other setting.
/// </para>
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
    /// <summary>Chat Completions reasoning_effort sent for models that support it (null = not sent).</summary>
    public string? ReasoningEffort { get; set; }
    /// <summary>Global switch for paid generation (the deterministic advisor and history stay available when off).</summary>
    public bool GenerationEnabled { get; set; } = true;
    public int UserRequestsPerDay { get; set; } = RagCostDefaults.UserRequestsPerDay;
    public int UserConcurrency { get; set; } = RagCostDefaults.UserConcurrency;
    public int GlobalConcurrency { get; set; } = RagCostDefaults.GlobalConcurrency;
    public int MaxQuestionCharacters { get; set; } = RagCostDefaults.MaxQuestionCharacters;
    public int GenerationTimeoutSeconds { get; set; } = RagCostDefaults.GenerationTimeoutSeconds;
    public decimal DailyBudgetUsd { get; set; } = RagCostDefaults.DailyBudgetUsd;
    public decimal MonthlyBudgetUsd { get; set; } = RagCostDefaults.MonthlyBudgetUsd;
    public decimal MonthlyIngestionBudgetUsd { get; set; } = RagCostDefaults.MonthlyIngestionBudgetUsd;
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
        MaxHistoryMessages, PromptVersion, ReasoningEffort, GenerationEnabled, UserRequestsPerDay, UserConcurrency, GlobalConcurrency, MaxQuestionCharacters,
        GenerationTimeoutSeconds, DailyBudgetUsd, MonthlyBudgetUsd, MonthlyIngestionBudgetUsd);

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
        ReasoningEffort = v.ReasoningEffort;
        GenerationEnabled = v.GenerationEnabled;
        UserRequestsPerDay = v.UserRequestsPerDay;
        UserConcurrency = v.UserConcurrency;
        GlobalConcurrency = v.GlobalConcurrency;
        MaxQuestionCharacters = v.MaxQuestionCharacters;
        GenerationTimeoutSeconds = v.GenerationTimeoutSeconds;
        DailyBudgetUsd = v.DailyBudgetUsd;
        MonthlyBudgetUsd = v.MonthlyBudgetUsd;
        MonthlyIngestionBudgetUsd = v.MonthlyIngestionBudgetUsd;
    }
}

/// <summary>Initial cost-control values (2026-11-17 policy). They are defaults, not ceilings: see <see cref="RagConfigurationLimits.Hard"/>.</summary>
public static class RagCostDefaults
{
    public const int UserRequestsPerDay = 30;
    public const int UserConcurrency = 1;
    public const int GlobalConcurrency = 3;
    public const int MaxQuestionCharacters = 1000;
    public const int GenerationTimeoutSeconds = 30;
    public const decimal DailyBudgetUsd = 1.00m;
    public const decimal MonthlyBudgetUsd = 4.00m;
    public const decimal MonthlyIngestionBudgetUsd = 0.50m;
    public const int MaxContextTokens = 2000;
    public const int MaxOutputTokens = 300;
}

public static class RagReasoningEfforts
{
    public static readonly IReadOnlyList<string> All = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];
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

/// <summary>
/// One allowlisted generation model (configuration <c>Rag:GenerationModels</c>) with its capabilities and its price. A model is usable
/// (activatable, and used for the bootstrap) only when it is <see cref="Enabled"/> and, for a billed provider, priced: spending is
/// estimated and reserved from these prices before every call, so an unpriced cloud model could never be bounded.
/// </summary>
public sealed class RagGenerationModelOptions
{
    public string Id { get; set; } = "";
    public string? DisplayName { get; set; }
    /// <summary>
    /// "openai" (billed) or "ollama" (local inference, not billed). Unset = inferred from the id: an Ollama tag (<c>name:tag</c>, e.g.
    /// <c>qwen3:1.7b</c>) is local, anything else is a billed cloud model. Registries written before this setting existed keep working.
    /// </summary>
    public string? Provider { get; set; }

    public string EffectiveProvider => Provider ?? (Id.Contains(':') ? RagProviders.Ollama : RagProviders.OpenAi);
    public bool Enabled { get; set; } = true;
    public bool SupportsTemperature { get; set; } = true;
    /// <summary><c>max_tokens</c> (classic chat models) or <c>max_completion_tokens</c> (reasoning-style models).</summary>
    public string TokenParameter { get; set; } = RagTokenParameters.MaxTokens;
    public int MaxOutputTokensLimit { get; set; } = 1500;
    /// <summary>Allowed reasoning_effort values (empty = the model takes none); <see cref="DefaultReasoningEffort"/> is used when a version sets none.</summary>
    public List<string> ReasoningEfforts { get; set; } = [];
    public string? DefaultReasoningEffort { get; set; }
    /// <summary>USD per 1M tokens (standard tier). Null = not priced: a billed model without prices cannot be used.</summary>
    public decimal? InputUsdPerMillion { get; set; }
    public decimal? CachedInputUsdPerMillion { get; set; }
    public decimal? OutputUsdPerMillion { get; set; }
    /// <summary>Where and when the prices were read (shown to admins; reviewing them is a manual step).</summary>
    public string? PricingSource { get; set; }
    public string? PricingEffectiveDate { get; set; }

    public bool Billed => EffectiveProvider == RagProviders.OpenAi;
    public bool Priced => !Billed || (InputUsdPerMillion is >= 0 && OutputUsdPerMillion is >= 0);
    public bool Usable => Enabled && Priced;
    /// <summary>Blended price used to pick the least expensive model (input + output per 1M).</summary>
    public decimal UnitCost => (InputUsdPerMillion ?? 0) + (OutputUsdPerMillion ?? 0);
}

/// <summary>An embedding model and its price (configuration <c>Rag:EmbeddingModels</c>).</summary>
public sealed class RagEmbeddingModelOptions
{
    public string Id { get; set; } = "";
    public string Provider { get; set; } = RagProviders.OpenAi;
    public decimal? InputUsdPerMillion { get; set; }
    public List<int> Dimensions { get; set; } = [];
    public string? PricingSource { get; set; }
    public string? PricingEffectiveDate { get; set; }
}

public static class RagProviders
{
    public const string OpenAi = "openai";
    public const string Ollama = "ollama";
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
    // Cost controls: an admin can lower them freely and raise them only up to these ceilings (no "unlimited" value exists).
    public int UserRequestsPerDayMin { get; set; } = 1;
    public int UserRequestsPerDayMax { get; set; } = 200;
    public int UserConcurrencyMin { get; set; } = 1;
    public int UserConcurrencyMax { get; set; } = 2;
    public int GlobalConcurrencyMin { get; set; } = 1;
    public int GlobalConcurrencyMax { get; set; } = 10;
    public int MaxQuestionCharactersMin { get; set; } = 100;
    public int MaxQuestionCharactersMax { get; set; } = 2000;
    public int GenerationTimeoutSecondsMin { get; set; } = 5;
    public int GenerationTimeoutSecondsMax { get; set; } = 60;
    public decimal DailyBudgetUsdMax { get; set; } = 10m;
    public decimal MonthlyBudgetUsdMax { get; set; } = 100m;
    public decimal MonthlyIngestionBudgetUsdMax { get; set; } = 10m;

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
        (r.UserRequestsPerDayMin, r.UserRequestsPerDayMax) = I(UserRequestsPerDayMin, UserRequestsPerDayMax, h.UserRequestsPerDayMin, h.UserRequestsPerDayMax);
        (r.UserConcurrencyMin, r.UserConcurrencyMax) = I(UserConcurrencyMin, UserConcurrencyMax, h.UserConcurrencyMin, h.UserConcurrencyMax);
        (r.GlobalConcurrencyMin, r.GlobalConcurrencyMax) = I(GlobalConcurrencyMin, GlobalConcurrencyMax, h.GlobalConcurrencyMin, h.GlobalConcurrencyMax);
        (r.MaxQuestionCharactersMin, r.MaxQuestionCharactersMax) = I(MaxQuestionCharactersMin, MaxQuestionCharactersMax, h.MaxQuestionCharactersMin, h.MaxQuestionCharactersMax);
        (r.GenerationTimeoutSecondsMin, r.GenerationTimeoutSecondsMax) = I(GenerationTimeoutSecondsMin, GenerationTimeoutSecondsMax, h.GenerationTimeoutSecondsMin,
            h.GenerationTimeoutSecondsMax);
        r.DailyBudgetUsdMax = Math.Clamp(DailyBudgetUsdMax, 0m, h.DailyBudgetUsdMax);
        r.MonthlyBudgetUsdMax = Math.Clamp(MonthlyBudgetUsdMax, 0m, h.MonthlyBudgetUsdMax);
        r.MonthlyIngestionBudgetUsdMax = Math.Clamp(MonthlyIngestionBudgetUsdMax, 0m, h.MonthlyIngestionBudgetUsdMax);
        return r;
    }
}

/// <summary>The tunable values of one configuration (generation, retrieval, chunking, and the cost controls).</summary>
public sealed record RagConfigurationValues(string GenerationModel, decimal? Temperature, int MaxOutputTokens, int ChunkCharacters, int ChunkOverlap, int TopK,
    decimal MinScore, int MaxContextTokens, int MaxHistoryMessages, string PromptVersion, string? ReasoningEffort = null, bool GenerationEnabled = true,
    int UserRequestsPerDay = RagCostDefaults.UserRequestsPerDay, int UserConcurrency = RagCostDefaults.UserConcurrency,
    int GlobalConcurrency = RagCostDefaults.GlobalConcurrency, int MaxQuestionCharacters = RagCostDefaults.MaxQuestionCharacters,
    int GenerationTimeoutSeconds = RagCostDefaults.GenerationTimeoutSeconds, decimal DailyBudgetUsd = RagCostDefaults.DailyBudgetUsd,
    decimal MonthlyBudgetUsd = RagCostDefaults.MonthlyBudgetUsd, decimal MonthlyIngestionBudgetUsd = RagCostDefaults.MonthlyIngestionBudgetUsd)
{
    public bool SameChunking(int? chunkCharacters, int? chunkOverlap) => chunkCharacters == ChunkCharacters && chunkOverlap == ChunkOverlap;
}

/// <summary>What the chatbot and ingestion use right now: the ACTIVE row, or the bootstrap values (version 0) from RagOptions.</summary>
/// <param name="ModelRegistered">
/// False when the ACTIVE row names a generation model that is no longer in <c>Rag:GenerationModels</c> (for example a cloud model after
/// the deployment switched to local inference): that model must not be requested any more.
/// </param>
public sealed record EffectiveRagConfiguration(int Version, bool Bootstrap, RagConfigurationValues Values, string TokenParameter, bool ModelRegistered = true)
{
    /// <summary>The registry entry of the generation model actually requested (null when unregistered: the RAG service default is used).</summary>
    public RagGenerationModelOptions? Model { get; init; }
}

/// <summary>Model registry, prompt-version allowlist and bounds; validates admin values server-side.</summary>
public sealed partial class RagConfigurationRules
{
    /// <summary>
    /// Built-in registry (used when <c>Rag:GenerationModels</c> is empty). Prices are the OpenAI standard-tier list prices read from
    /// developers.openai.com/api/docs/pricing and /models/gpt-6-luna on 2026-11-17. gpt-6-luna: Chat Completions, reasoning_effort
    /// none|low|medium|high|xhigh|max (sent as "none": no reasoning tokens), temperature only without reasoning (so never sent),
    /// max_completion_tokens. Models whose prices were not verified are listed DISABLED: they cannot be activated until an operator
    /// adds verified prices in configuration.
    /// </summary>
    public static readonly IReadOnlyList<RagGenerationModelOptions> DefaultModels =
    [
        new()
        {
            Id = "gpt-6-luna", DisplayName = "GPT-6 Luna", Provider = RagProviders.OpenAi, SupportsTemperature = false,
            TokenParameter = RagTokenParameters.MaxCompletionTokens, MaxOutputTokensLimit = 1500,
            ReasoningEfforts = ["none", "low", "medium", "high", "xhigh", "max"], DefaultReasoningEffort = "none",
            InputUsdPerMillion = 0.10m, CachedInputUsdPerMillion = 0.01m, OutputUsdPerMillion = 0.50m,
            PricingSource = "https://developers.openai.com/api/docs/pricing (standard tier, short context)", PricingEffectiveDate = "2026-11-17",
        },
        new() { Id = "gpt-4.1-mini", DisplayName = "GPT-4.1 mini", Enabled = false, SupportsTemperature = true, TokenParameter = RagTokenParameters.MaxTokens, MaxOutputTokensLimit = 1500 },
        new() { Id = "gpt-4.1", DisplayName = "GPT-4.1", Enabled = false, SupportsTemperature = true, TokenParameter = RagTokenParameters.MaxTokens, MaxOutputTokensLimit = 1500 },
        new() { Id = "gpt-4o-mini", DisplayName = "GPT-4o mini", Enabled = false, SupportsTemperature = true, TokenParameter = RagTokenParameters.MaxTokens, MaxOutputTokensLimit = 1500 },
    ];

    /// <summary>Built-in embedding prices (used when <c>Rag:EmbeddingModels</c> is empty).</summary>
    public static readonly IReadOnlyList<RagEmbeddingModelOptions> DefaultEmbeddingModels =
    [
        new()
        {
            Id = "text-embedding-3-small", Provider = RagProviders.OpenAi, InputUsdPerMillion = 0.02m, Dimensions = [1536, 1024],
            PricingSource = "https://developers.openai.com/api/docs/pricing", PricingEffectiveDate = "2026-11-17",
        },
        new() { Id = "qwen3-embedding:0.6b", Provider = RagProviders.Ollama, InputUsdPerMillion = 0m, Dimensions = [1024] },
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
            if (m.Provider is not (null or RagProviders.OpenAi or RagProviders.Ollama)) problems.Add($"model '{m.Id}' provider must be openai or ollama");
            if (m.ReasoningEfforts.Any(e => !RagReasoningEfforts.All.Contains(e))) problems.Add($"model '{m.Id}' reasoningEfforts must be among {string.Join(", ", RagReasoningEfforts.All)}");
            if (m.DefaultReasoningEffort is { } d && !m.ReasoningEfforts.Contains(d)) problems.Add($"model '{m.Id}' defaultReasoningEffort must be one of its reasoningEfforts");
            if (m.InputUsdPerMillion < 0 || m.OutputUsdPerMillion < 0 || m.CachedInputUsdPerMillion < 0) problems.Add($"model '{m.Id}' prices cannot be negative");
            if (m.SupportsTemperature && m.ReasoningEfforts.Any(e => e != "none") && m.DefaultReasoningEffort is not (null or "none"))
                problems.Add($"model '{m.Id}' cannot send temperature with reasoning on by default");
        }
        if (models.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != models.Count) problems.Add("model ids must be unique");
        var prompts = options.PromptVersions is { Count: > 0 } p ? p.Distinct(StringComparer.Ordinal).ToList() : ["v1"];
        if (prompts.Any(v => !PromptVersionPattern().IsMatch(v ?? ""))) problems.Add("prompt versions must match ^[A-Za-z0-9._-]{1,20}$");
        if (problems.Count > 0) throw new InvalidOperationException("Invalid 'Rag:GenerationModels' / 'Rag:PromptVersions': " + string.Join("; ", problems));
        Models = models.ToList();
        EmbeddingModels = options.EmbeddingModels is { Count: > 0 } embeddings ? embeddings.ToList() : DefaultEmbeddingModels.ToList();
        PromptVersions = prompts;
        Limits = (options.ConfigurationLimits ?? new RagConfigurationLimits()).Narrowed();
    }

    public IReadOnlyList<RagEmbeddingModelOptions> EmbeddingModels { get; }

    public RagEmbeddingModelOptions? FindEmbeddingModel(string? id) => EmbeddingModels.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));

    /// <summary>The least expensive usable generation model (the bootstrap default; a more expensive one needs an explicit admin activation).</summary>
    public RagGenerationModelOptions? CheapestUsableModel()
        => Models.Where(m => m.Usable).OrderBy(m => m.Billed ? m.UnitCost : 0).ThenBy(m => m.Id, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>The reasoning effort actually sent: the version's value, else the model's default; never one the model does not accept.</summary>
    public string? ReasoningEffortFor(RagGenerationModelOptions? model, string? configured)
        => model is null || model.ReasoningEfforts.Count == 0 ? null
            : configured is not null && model.ReasoningEfforts.Contains(configured) ? configured : model.DefaultReasoningEffort;

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
        if (model is null) Add("generationModel", $"Unknown model. Allowed: {string.Join(", ", Models.Where(m => m.Usable).Select(m => m.Id))}.");
        else if (!model.Enabled) Add("generationModel", "This model is disabled in the model registry.");
        else if (!model.Priced) Add("generationModel", "This model has no verified price: spending could not be bounded, so it cannot be activated.");
        if (v.ReasoningEffort is { } effort && (model is null || !model.ReasoningEfforts.Contains(effort)))
            Add("reasoningEffort", model is { ReasoningEfforts.Count: > 0 } ? $"Allowed: {string.Join(", ", model.ReasoningEfforts)}." : "This model takes no reasoning effort.");
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
        void Range(string field, int value, int min, int max) { if (value < min || value > max) Add(field, $"Must be between {min} and {max}."); }
        Range("userRequestsPerDay", v.UserRequestsPerDay, l.UserRequestsPerDayMin, l.UserRequestsPerDayMax);
        Range("userConcurrency", v.UserConcurrency, l.UserConcurrencyMin, l.UserConcurrencyMax);
        Range("globalConcurrency", v.GlobalConcurrency, l.GlobalConcurrencyMin, l.GlobalConcurrencyMax);
        Range("maxQuestionCharacters", v.MaxQuestionCharacters, l.MaxQuestionCharactersMin, l.MaxQuestionCharactersMax);
        Range("generationTimeoutSeconds", v.GenerationTimeoutSeconds, l.GenerationTimeoutSecondsMin, l.GenerationTimeoutSecondsMax);
        void Usd(string field, decimal value, decimal max)
        {
            if (value < 0 || value > max) Add(field, $"Must be between 0 and {max} USD.");
            else if (decimal.Round(value, 2) != value) Add(field, "At most 2 decimal places.");
        }
        Usd("dailyBudgetUsd", v.DailyBudgetUsd, l.DailyBudgetUsdMax);
        Usd("monthlyBudgetUsd", v.MonthlyBudgetUsd, l.MonthlyBudgetUsdMax);
        Usd("monthlyIngestionBudgetUsd", v.MonthlyIngestionBudgetUsd, l.MonthlyIngestionBudgetUsdMax);
        if (v.DailyBudgetUsd > v.MonthlyBudgetUsd) Add("dailyBudgetUsd", "Cannot exceed the monthly budget.");
        return errors;
    }

    /// <summary>The bootstrap configuration (version 0) from RagOptions.</summary>
    /// <summary>
    /// The bootstrap configuration (version 0) from RagOptions. Without <c>Rag:GenerationModel</c> it is the least expensive usable model;
    /// the cost controls come from RagOptions (defaults <see cref="RagCostDefaults"/>), clamped to the hard ceilings.
    /// </summary>
    public EffectiveRagConfiguration Bootstrap(RagOptions options)
    {
        var configured = string.IsNullOrWhiteSpace(options.GenerationModel) ? null : FindModel(options.GenerationModel.Trim());
        var model = configured is { Usable: true } ? configured : CheapestUsableModel() ?? configured;
        var id = model?.Id ?? options.GenerationModel ?? "";
        var l = Limits;
        var values = new RagConfigurationValues(id, model is { SupportsTemperature: false } ? null : options.Temperature, options.MaxOutputTokens,
            options.ChunkCharacters, options.ChunkOverlap, options.TopK, options.MinScore, options.MaxContextTokens, options.MaxHistoryMessages, options.PromptVersion,
            ReasoningEffortFor(model, null), options.GenerationEnabled,
            Math.Clamp(options.DailyRequestsPerUser, l.UserRequestsPerDayMin, l.UserRequestsPerDayMax),
            Math.Clamp(options.MaxConcurrentGenerationsPerUser, l.UserConcurrencyMin, l.UserConcurrencyMax),
            Math.Clamp(options.GlobalConcurrentGenerations, l.GlobalConcurrencyMin, l.GlobalConcurrencyMax),
            Math.Clamp(options.MaxQuestionCharacters, l.MaxQuestionCharactersMin, l.MaxQuestionCharactersMax),
            Math.Clamp(options.RequestTimeoutSeconds, l.GenerationTimeoutSecondsMin, l.GenerationTimeoutSecondsMax),
            Math.Clamp(options.DailyBudgetUsd, 0, l.DailyBudgetUsdMax), Math.Clamp(options.MonthlyBudgetUsd, 0, l.MonthlyBudgetUsdMax),
            Math.Clamp(options.MonthlyIngestionBudgetUsd, 0, l.MonthlyIngestionBudgetUsdMax));
        return new EffectiveRagConfiguration(0, true, values, TokenParameterFor(id), model is { Usable: true }) { Model = model is { Usable: true } ? model : null };
    }

    /// <summary>
    /// The ACTIVE row. A row whose model left the registry or was disabled / unpriced since is not requested any more (the RAG service default
    /// applies, and is costed at the least expensive usable model's price); every cost control of the row still applies.
    /// </summary>
    public EffectiveRagConfiguration Effective(RagConfiguration active)
    {
        var model = FindModel(active.GenerationModel);
        var usable = model is { Usable: true };
        var values = active.Values() with { ReasoningEffort = usable ? ReasoningEffortFor(model, active.ReasoningEffort) : null };
        return new EffectiveRagConfiguration(active.Version, false, values, TokenParameterFor(active.GenerationModel), usable) { Model = usable ? model : null };
    }
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
