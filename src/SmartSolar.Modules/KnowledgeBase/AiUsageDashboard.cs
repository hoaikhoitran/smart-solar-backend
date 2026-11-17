using SmartSolar.Modules.Common.Results;

namespace SmartSolar.Modules.KnowledgeBase;

/// <summary>One ledger row as the dashboard reads it (no question, answer or key exists in the ledger).</summary>
public sealed record AiUsageRow(Guid Id, string Operation, string Status, string Outcome, Guid? UserId, string? UserRole, string? EmbeddingModel, long EmbeddingTokens,
    decimal EmbeddingCostUsd, string? GenerationModel, long InputTokens, long CachedInputTokens, long OutputTokens, long ReasoningTokens, decimal GenerationCostUsd,
    decimal ReservedUsd, decimal CostUsd, bool UsageReported, int? LatencyMs, int? ConfigVersion, DateTimeOffset CreatedAt, DateTimeOffset? SettledAt,
    Guid? ReviewedByUserId, DateTimeOffset? ReviewedAt, string? ReviewNote);

/// <summary>Read side of the AI cost ledger (server-side filtering; aggregation over a bounded date range).</summary>
public interface IAiUsageQueries
{
    /// <summary>Rows created in [from, to), at most <paramref name="cap"/> (+1 so the caller can tell that the range is too large).</summary>
    Task<IReadOnlyList<AiUsageRow>> ListAsync(DateTimeOffset from, DateTimeOffset to, int cap, CancellationToken cancellationToken);

    Task<(IReadOnlyList<AiUsageRow> Items, int Total)> PageAsync(DateTimeOffset from, DateTimeOffset to, string? operation, string? status, string? outcome, Guid? userId,
        int skip, int take, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, string>> UserNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}

public sealed record AiUsageRangeView(DateOnly From, DateOnly To);
public sealed record AiMoneyView(decimal SettledUsd, decimal UncertainUsd, decimal ReservedUsd, decimal TotalUsd);
public sealed record AiBudgetLineView(string Period, DateOnly PeriodStart, decimal LimitUsd, decimal SettledUsd, decimal UncertainUsd, decimal ReservedUsd,
    decimal RemainingUsd, decimal UsedPercent, int Requests, int Blocked);

/// <param name="Today">Chat spending of the current UTC day (settled + uncertain + reserved = what the budget counts).</param>
public sealed record AiUsageSummaryView(
    AiUsageRangeView Range, AiBudgetLineView Today, AiBudgetLineView Month, AiBudgetLineView IngestionMonth, AiMoneyView RangeCost,
    int Requests, int Answered, int InsufficientContext, int Failed, int Uncertain, int BlockedToday, long InputTokens, long CachedInputTokens, long OutputTokens,
    long ReasoningTokens, long EmbeddingTokens, int? AverageLatencyMs, int? P95LatencyMs, string? ActiveGenerationModel, string? ActiveEmbeddingModel,
    int ConfigVersion, bool GenerationEnabled);

public sealed record AiUsageDayView(DateOnly Date, decimal CostUsd, decimal GenerationCostUsd, decimal EmbeddingCostUsd, decimal IngestionCostUsd, int Requests,
    int Failed, long InputTokens, long OutputTokens, long EmbeddingTokens, int? P95LatencyMs);

public sealed record AiUsageBreakdownView(string Key, int Requests, decimal CostUsd, long InputTokens, long OutputTokens, long EmbeddingTokens);
public sealed record AiTopUserView(Guid UserId, string? FullName, string? Role, int Requests, decimal CostUsd, long Tokens);
public sealed record AiUsagePageView(IReadOnlyList<AiUsageRow> Items, int Total, int Page, int PageSize);

public sealed record AiBudgetAlertView(string Code, string Severity, string Message);

/// <summary>Budget administration: the active values, the hard ceilings, current spending and alerts.</summary>
public sealed record AiBudgetView(int ActiveVersion, bool Bootstrap, bool GenerationEnabled, decimal DailyBudgetUsd, decimal MonthlyBudgetUsd,
    decimal MonthlyIngestionBudgetUsd, int UserRequestsPerDay, int UserConcurrency, int GlobalConcurrency, int MaxQuestionCharacters, int GenerationTimeoutSeconds,
    RagConfigurationLimitsView Ceilings, AiBudgetLineView Today, AiBudgetLineView Month, AiBudgetLineView IngestionMonth, IReadOnlyList<AiBudgetAlertView> Alerts,
    string? ActiveGenerationModel, string? ActiveEmbeddingModel);

/// <summary>
/// Admin cost dashboard over the AI ledger. Ranges are UTC dates, at most <see cref="MaxRangeDays"/> days; breakdowns are aggregated on the
/// server; the top-users list names users (Admin only). Uncertain charges are counted at their reserved amount until reviewed.
/// </summary>
public sealed class AiUsageDashboardService(IAiUsageQueries queries, IKnowledgeUnitOfWork unitOfWork, IRagConfigurationProvider configuration, RagConfigurationService configService,
    RagOptions options, TimeProvider time)
{
    public const int MaxRangeDays = 92;
    public const int MaxRows = 200_000;

    public async Task<OperationResult<AiUsageSummaryView>> SummaryAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        if (Range(from, to) is not { } range) return InvalidRange<AiUsageSummaryView>();
        var rows = await RowsAsync(range, cancellationToken);
        if (rows is null) return TooMany<AiUsageSummaryView>();
        var config = await configuration.GetAsync(cancellationToken);
        var (today, month, ingest) = await LinesAsync(config.Values, cancellationToken);
        var chat = rows.Where(r => r.Operation == AiUsageOperations.Chat).ToList();
        var latencies = chat.Where(r => r.LatencyMs is not null).Select(r => r.LatencyMs!.Value).OrderBy(x => x).ToList();
        return OperationResult<AiUsageSummaryView>.Ok(new AiUsageSummaryView(
            new AiUsageRangeView(range.From, range.To), today, month, ingest, Money(rows), chat.Count,
            chat.Count(r => r.Outcome == AiUsageOutcomes.Answered), chat.Count(r => r.Outcome == AiUsageOutcomes.InsufficientContext),
            chat.Count(r => r.Outcome is AiUsageOutcomes.ProviderError or AiUsageOutcomes.ProviderRateLimited or AiUsageOutcomes.Timeout or AiUsageOutcomes.Busy or AiUsageOutcomes.Expired),
            rows.Count(r => r.Status == AiUsageStatuses.Uncertain), today.Blocked,
            chat.Sum(r => r.InputTokens), chat.Sum(r => r.CachedInputTokens), chat.Sum(r => r.OutputTokens), chat.Sum(r => r.ReasoningTokens), rows.Sum(r => r.EmbeddingTokens),
            latencies.Count == 0 ? null : (int)latencies.Average(), Percentile(latencies, 95), config.Model?.Id ?? (config.Bootstrap ? null : config.Values.GenerationModel),
            options.EmbeddingModel, config.Version, config.Values.GenerationEnabled));
    }

    public async Task<OperationResult<IReadOnlyList<AiUsageDayView>>> TimeseriesAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        if (Range(from, to) is not { } range) return InvalidRange<IReadOnlyList<AiUsageDayView>>();
        var rows = await RowsAsync(range, cancellationToken);
        if (rows is null) return TooMany<IReadOnlyList<AiUsageDayView>>();
        var byDay = rows.GroupBy(r => DateOnly.FromDateTime(r.CreatedAt.UtcDateTime)).ToDictionary(g => g.Key, g => g.ToList());
        var days = new List<AiUsageDayView>();
        for (var d = range.From; d <= range.To; d = d.AddDays(1))
        {
            var list = byDay.GetValueOrDefault(d) ?? [];
            var chat = list.Where(r => r.Operation == AiUsageOperations.Chat).ToList();
            var latencies = chat.Where(r => r.LatencyMs is not null).Select(r => r.LatencyMs!.Value).OrderBy(x => x).ToList();
            days.Add(new AiUsageDayView(d, list.Sum(Charged), chat.Sum(r => Settled(r) ? r.GenerationCostUsd : 0), chat.Sum(r => Settled(r) ? r.EmbeddingCostUsd : 0),
                list.Where(r => r.Operation == AiUsageOperations.Ingestion).Sum(Charged), chat.Count,
                chat.Count(r => r.Outcome is not (AiUsageOutcomes.Answered or AiUsageOutcomes.InsufficientContext or AiUsageOutcomes.Pending)),
                chat.Sum(r => r.InputTokens), chat.Sum(r => r.OutputTokens), list.Sum(r => r.EmbeddingTokens), Percentile(latencies, 95)));
        }
        return OperationResult<IReadOnlyList<AiUsageDayView>>.Ok(days);
    }

    /// <summary><paramref name="dimension"/>: model, operation (query embedding / generation / ingestion) or role.</summary>
    public async Task<OperationResult<IReadOnlyList<AiUsageBreakdownView>>> BreakdownAsync(string dimension, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        if (Range(from, to) is not { } range) return InvalidRange<IReadOnlyList<AiUsageBreakdownView>>();
        var rows = await RowsAsync(range, cancellationToken);
        if (rows is null) return TooMany<IReadOnlyList<AiUsageBreakdownView>>();
        IEnumerable<AiUsageBreakdownView> result = dimension switch
        {
            "model" => rows.SelectMany(r => new[]
                {
                    (Key: r.GenerationModel, Cost: Settled(r) ? r.GenerationCostUsd : 0m, In: r.InputTokens, Out: r.OutputTokens, Emb: 0L, Count: r.GenerationModel is null ? 0 : 1),
                    (Key: r.EmbeddingModel, Cost: Settled(r) ? r.EmbeddingCostUsd : 0m, In: 0L, Out: 0L, Emb: r.EmbeddingTokens, Count: r.EmbeddingModel is null ? 0 : 1),
                })
                .Where(x => x.Key is not null)
                .GroupBy(x => x.Key!).Select(g => new AiUsageBreakdownView(g.Key, g.Sum(x => x.Count), g.Sum(x => x.Cost), g.Sum(x => x.In), g.Sum(x => x.Out), g.Sum(x => x.Emb))),
            "operation" => new[]
            {
                new AiUsageBreakdownView("QUERY_EMBEDDING", rows.Count(r => r.Operation == AiUsageOperations.Chat && r.EmbeddingTokens > 0),
                    rows.Where(r => r.Operation == AiUsageOperations.Chat && Settled(r)).Sum(r => r.EmbeddingCostUsd), 0, 0,
                    rows.Where(r => r.Operation == AiUsageOperations.Chat).Sum(r => r.EmbeddingTokens)),
                new AiUsageBreakdownView("GENERATION", rows.Count(r => r.Operation == AiUsageOperations.Chat && r.GenerationModel is not null),
                    rows.Where(r => r.Operation == AiUsageOperations.Chat && Settled(r)).Sum(r => r.GenerationCostUsd),
                    rows.Sum(r => r.InputTokens), rows.Sum(r => r.OutputTokens), 0),
                new AiUsageBreakdownView("INGESTION", rows.Count(r => r.Operation == AiUsageOperations.Ingestion),
                    rows.Where(r => r.Operation == AiUsageOperations.Ingestion).Sum(Charged), 0, 0,
                    rows.Where(r => r.Operation == AiUsageOperations.Ingestion).Sum(r => r.EmbeddingTokens)),
                new AiUsageBreakdownView("UNCERTAIN", rows.Count(r => r.Status == AiUsageStatuses.Uncertain),
                    rows.Where(r => r.Status == AiUsageStatuses.Uncertain).Sum(r => r.CostUsd), 0, 0, 0),
            },
            "role" => rows.Where(r => r.Operation == AiUsageOperations.Chat).GroupBy(r => r.UserRole ?? "UNKNOWN")
                .Select(g => new AiUsageBreakdownView(g.Key, g.Count(), g.Sum(Charged), g.Sum(r => r.InputTokens), g.Sum(r => r.OutputTokens), g.Sum(r => r.EmbeddingTokens))),
            _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
        };
        return OperationResult<IReadOnlyList<AiUsageBreakdownView>>.Ok(result.OrderByDescending(x => x.CostUsd).ThenBy(x => x.Key, StringComparer.Ordinal).ToList());
    }

    public async Task<OperationResult<IReadOnlyList<AiTopUserView>>> TopUsersAsync(DateOnly? from, DateOnly? to, int limit, CancellationToken cancellationToken)
    {
        if (Range(from, to) is not { } range) return InvalidRange<IReadOnlyList<AiTopUserView>>();
        var rows = await RowsAsync(range, cancellationToken);
        if (rows is null) return TooMany<IReadOnlyList<AiTopUserView>>();
        var top = rows.Where(r => r.UserId is not null).GroupBy(r => r.UserId!.Value)
            .Select(g => (Id: g.Key, Role: g.Select(r => r.UserRole).LastOrDefault(r => r is not null), Requests: g.Count(), Cost: g.Sum(Charged),
                Tokens: g.Sum(r => r.InputTokens + r.OutputTokens + r.EmbeddingTokens)))
            .OrderByDescending(x => x.Cost).ThenByDescending(x => x.Requests).Take(Math.Clamp(limit, 1, 50)).ToList();
        var names = await queries.UserNamesAsync(top.Select(t => t.Id).ToList(), cancellationToken);
        return OperationResult<IReadOnlyList<AiTopUserView>>.Ok(top.Select(t => new AiTopUserView(t.Id, names.GetValueOrDefault(t.Id), t.Role, t.Requests, t.Cost, t.Tokens)).ToList());
    }

    public async Task<OperationResult<AiUsagePageView>> RequestsAsync(DateOnly? from, DateOnly? to, string? operation, string? status, string? outcome, Guid? userId,
        int page, int pageSize, CancellationToken cancellationToken)
    {
        if (Range(from, to) is not { } range) return InvalidRange<AiUsagePageView>();
        if (operation is not null && !AiUsageOperations.All.Contains(operation)) return Invalid<AiUsagePageView>("operation", AiUsageOperations.All);
        if (status is not null && !AiUsageStatuses.All.Contains(status)) return Invalid<AiUsagePageView>("status", AiUsageStatuses.All);
        if (outcome is not null && !AiUsageOutcomes.All.Contains(outcome)) return Invalid<AiUsagePageView>("outcome", AiUsageOutcomes.All);
        page = Math.Clamp(page, 1, 10_000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var (items, total) = await queries.PageAsync(Start(range.From), Start(range.To.AddDays(1)), operation, status, outcome, userId, (page - 1) * pageSize, pageSize,
            cancellationToken);
        return OperationResult<AiUsagePageView>.Ok(new AiUsagePageView(items, total, page, pageSize));
    }

    public async Task<AiBudgetView> BudgetAsync(CancellationToken cancellationToken)
    {
        var config = await configuration.GetAsync(cancellationToken);
        var v = config.Values;
        var (today, month, ingest) = await LinesAsync(v, cancellationToken);
        var alerts = new List<AiBudgetAlertView>();
        void Line(AiBudgetLineView line, string name)
        {
            if (line.LimitUsd > 0 && line.UsedPercent >= 100) alerts.Add(new($"{name}_BUDGET_EXHAUSTED", "critical", $"The {name.ToLowerInvariant()} budget is used up: paid calls are refused."));
            else if (line.UsedPercent >= 80) alerts.Add(new($"{name}_BUDGET_80", "warning", $"{line.UsedPercent:0}% of the {name.ToLowerInvariant()} budget is used."));
            if (line.UncertainUsd > 0) alerts.Add(new($"{name}_UNCERTAIN", "warning", $"{line.UncertainUsd} USD of uncertain charges wait for review."));
        }
        Line(today, "DAILY");
        Line(month, "MONTHLY");
        Line(ingest, "INGESTION");
        if (!v.GenerationEnabled) alerts.Add(new("GENERATION_DISABLED", "info", "Paid generation is switched off."));
        if (!config.Bootstrap && !config.ModelRegistered)
            alerts.Add(new("MODEL_NOT_USABLE", "warning", $"The active model {v.GenerationModel} is not a usable registry model; the RAG service default runs, costed at the highest price."));
        return new AiBudgetView(config.Version, config.Bootstrap, v.GenerationEnabled, v.DailyBudgetUsd, v.MonthlyBudgetUsd, v.MonthlyIngestionBudgetUsd, v.UserRequestsPerDay,
            v.UserConcurrency, v.GlobalConcurrency, v.MaxQuestionCharacters, v.GenerationTimeoutSeconds, configService.Catalog().Limits, today, month, ingest, alerts,
            config.Model?.Id, options.EmbeddingModel);
    }

    public async Task<OperationResult<bool>> ReviewAsync(Guid adminUserId, Guid ledgerId, decimal? costUsd, string? note, CancellationToken cancellationToken)
    {
        var why = note?.Trim();
        if (costUsd is not { } cost || cost < 0 || cost > 100 || decimal.Round(cost, 6) != cost || string.IsNullOrEmpty(why) || why.Length > 500)
            return OperationResult<bool>.Fail(CommonErrorCodes.ValidationFailed, "costUsd (0–100, at most 6 decimals) and a note (1–500 characters) are required.");
        return await unitOfWork.ReviewUncertainAsync(ledgerId, cost, adminUserId, why, time.GetUtcNow(), cancellationToken)
            ? OperationResult<bool>.Ok(true)
            : OperationResult<bool>.Fail(AiCostErrorCodes.UsageNotFound, "No UNCERTAIN charge with this id (it was reviewed already, or never uncertain).");
    }

    // ------------------------------------------------------------------ internals

    private async Task<(AiBudgetLineView Today, AiBudgetLineView Month, AiBudgetLineView Ingestion)> LinesAsync(RagConfigurationValues v, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var month = new DateOnly(today.Year, today.Month, 1);
        var periods = await unitOfWork.GetBudgetPeriodsAsync([(AiBudgetPeriodKinds.Day, today), (AiBudgetPeriodKinds.Month, month), (AiBudgetPeriodKinds.IngestMonth, month)],
            cancellationToken);
        return (Line(periods[0], v.DailyBudgetUsd), Line(periods[1], v.MonthlyBudgetUsd), Line(periods[2], v.MonthlyIngestionBudgetUsd));
    }

    private static AiBudgetLineView Line(AiBudgetPeriod p, decimal limit)
        => new(p.PeriodKind, p.PeriodStart, limit, p.SettledUsd, p.UncertainUsd, p.ReservedUsd, Math.Max(0, limit - p.CommittedUsd),
            limit <= 0 ? (p.CommittedUsd > 0 ? 100 : 0) : decimal.Round(p.CommittedUsd / limit * 100, 1), p.Requests, p.Blocked);

    private static bool Settled(AiUsageRow r) => r.Status is AiUsageStatuses.Settled or AiUsageStatuses.Uncertain;

    /// <summary>What a row costs the budget: settled cost, uncertain charge, or the open reservation.</summary>
    private static decimal Charged(AiUsageRow r) => r.Status == AiUsageStatuses.Reserved ? r.ReservedUsd : r.CostUsd;

    private static AiMoneyView Money(IReadOnlyList<AiUsageRow> rows)
    {
        var settled = rows.Where(r => r.Status == AiUsageStatuses.Settled).Sum(r => r.CostUsd);
        var uncertain = rows.Where(r => r.Status == AiUsageStatuses.Uncertain).Sum(r => r.CostUsd);
        var reserved = rows.Where(r => r.Status == AiUsageStatuses.Reserved).Sum(r => r.ReservedUsd);
        return new AiMoneyView(settled, uncertain, reserved, settled + uncertain + reserved);
    }

    private static int? Percentile(IReadOnlyList<int> sorted, int p)
        => sorted.Count == 0 ? null : sorted[Math.Clamp((int)Math.Ceiling(p / 100.0 * sorted.Count) - 1, 0, sorted.Count - 1)];

    private (DateOnly From, DateOnly To)? Range(DateOnly? from, DateOnly? to)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var end = to ?? today;
        var start = from ?? new DateOnly(end.Year, end.Month, 1);
        if (start > end || end.DayNumber - start.DayNumber + 1 > MaxRangeDays) return null;
        return (start, end);
    }

    private static DateTimeOffset Start(DateOnly d) => new(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private async Task<IReadOnlyList<AiUsageRow>?> RowsAsync((DateOnly From, DateOnly To) range, CancellationToken cancellationToken)
    {
        var rows = await queries.ListAsync(Start(range.From), Start(range.To.AddDays(1)), MaxRows, cancellationToken);
        return rows.Count > MaxRows ? null : rows;
    }

    private static OperationResult<T> InvalidRange<T>()
        => OperationResult<T>.Fail(CommonErrorCodes.ValidationFailed, $"'from' must not be after 'to' and the range is at most {MaxRangeDays} days (UTC dates, YYYY-MM-DD).");

    private static OperationResult<T> TooMany<T>()
        => OperationResult<T>.Fail(CommonErrorCodes.ValidationFailed, "Too many rows in this range; choose a shorter range.");

    private static OperationResult<T> Invalid<T>(string field, IReadOnlyList<string> allowed)
        => OperationResult<T>.Fail(CommonErrorCodes.ValidationFailed, $"'{field}' must be one of: {string.Join(", ", allowed)}.");
}
