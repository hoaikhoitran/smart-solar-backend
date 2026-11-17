using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.KnowledgeBase;

namespace SmartSolar.Infrastructure.Persistence.KnowledgeBase;

/// <summary>
/// AI cost control (2026-11-17). Strong consistency comes from PostgreSQL row locks, always taken in one order: the day's usage rows
/// (global, then user) and then the budget periods sorted by (kind, start). Every reservation, settlement, expiry and review updates the
/// period counters under those locks, so no number of concurrent requests or API replicas can commit more than a budget. If the database
/// is unavailable the admission throws: no provider call is ever made without a reservation (fail closed).
/// </summary>
public sealed partial class KnowledgeUnitOfWork
{
    public async Task<AiAdmission> TryAdmitAsync(AiAdmissionRequest r, RagOptions limits, CancellationToken cancellationToken)
    {
        // Crashed or abandoned work first (its own transaction): its reservation becomes an uncertain charge, never silently free.
        await ExpireAiReservationsAsync(r.Now, 100, cancellationToken);
        return await ExecuteInTransactionAsync<AiAdmission>(async token =>
        {
            var (user, global) = await LockLedgerAsync(r.UserId, r.Date, r.Now, token);
            var month = MonthOf(r.Date);
            var periods = await LockPeriodsAsync([(AiBudgetPeriodKinds.Day, r.Date), (AiBudgetPeriodKinds.Month, month)], r.Now, token);
            var day = periods[(AiBudgetPeriodKinds.Day, r.Date)];
            var monthly = periods[(AiBudgetPeriodKinds.Month, month)];

            // Expired, never-completed token reservations (the previous ledger) are charged at their estimate, as before.
            var expired = await Db.RagUsageReservations.Where(x => x.UserId == r.UserId && x.UsageDate == r.Date && x.ExpiresAt <= r.Now).ToListAsync(token);
            if (expired.Count > 0)
            {
                var estimate = expired.Sum(x => x.ReservedTokens);
                user.EstimatedTokens += estimate;
                global.EstimatedTokens += estimate;
                Db.RagUsageReservations.RemoveRange(expired);
            }

            var open = await Db.RagUsageReservations.AsNoTracking().Where(x => x.UsageDate == r.Date && x.ExpiresAt > r.Now)
                .Select(x => new { x.UserId, x.ReservedTokens, x.ReservedUsd }).ToListAsync(token);
            var mine = open.Where(x => x.UserId == r.UserId).ToList();

            string? limit = null;
            TimeSpan? retryAfter = null;
            var tomorrow = new DateTimeOffset(r.Date.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) - r.Now;
            var nextMonth = new DateTimeOffset(month.AddMonths(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) - r.Now;
            // The same question already being answered (double submit, retry, another replica) is reported as such, not as a quota.
            if (r.ClientMessageId is not null && await Db.RagUsageReservations.AsNoTracking()
                    .AnyAsync(x => x.UserId == r.UserId && x.ClientMessageId == r.ClientMessageId && x.ExpiresAt > r.Now, token))
                (limit, retryAfter) = (AiCostErrorCodes.RequestInProgress, TimeSpan.FromSeconds(3));
            else if (mine.Count >= Math.Max(1, r.UserConcurrency)) (limit, retryAfter) = (AiCostErrorCodes.Concurrency, TimeSpan.FromSeconds(5));
            else if (user.Requests >= r.UserRequestsPerDay) (limit, retryAfter) = (AiCostErrorCodes.UserQuotaExceeded, tomorrow);
            else if (user.ChargedTokens + mine.Sum(x => x.ReservedTokens) + r.ReservedTokens > limits.DailyTokensPerUser)
                (limit, retryAfter) = (AiCostErrorCodes.UserQuotaExceeded, tomorrow);
            else if (global.ChargedTokens + open.Sum(x => x.ReservedTokens) + r.ReservedTokens > limits.GlobalDailyTokens)
                (limit, retryAfter) = (AiCostErrorCodes.GlobalTokenQuota, tomorrow);
            else if (r.Billable && open.Count(x => x.ReservedUsd > 0) >= Math.Max(1, r.GlobalConcurrency))
                (limit, retryAfter) = (AiCostErrorCodes.GlobalBusy, TimeSpan.FromSeconds(5));
            else if (r.Billable && day.CommittedUsd + r.ReservedUsd > r.DailyBudgetUsd) (limit, retryAfter) = (AiCostErrorCodes.BudgetExceeded, tomorrow);
            else if (r.Billable && monthly.CommittedUsd + r.ReservedUsd > r.MonthlyBudgetUsd) (limit, retryAfter) = (AiCostErrorCodes.BudgetExceeded, nextMonth);

            if (limit is not null)
            {
                day.Blocked++;
                day.UpdatedAt = r.Now;
                await SaveChangesAsync(token);   // expired reservations are converted and the refusal counted even when refused
                return (new AiAdmission(null, limit, retryAfter), true);
            }

            var id = Guid.NewGuid();
            Db.RagUsageReservations.Add(new RagUsageReservation
            {
                Id = id, UserId = r.UserId, UsageDate = r.Date, ReservedTokens = Math.Max(1, r.ReservedTokens), ReservedUsd = r.ReservedUsd,
                ClientMessageId = r.ClientMessageId, CreatedAt = r.Now, ExpiresAt = r.ExpiresAt,
            });
            Db.AiUsageLedger.Add(new AiUsageLedgerEntry
            {
                Id = id, IdempotencyKey = $"chat:{id}", Operation = AiUsageOperations.Chat, Status = AiUsageStatuses.Reserved, Outcome = AiUsageOutcomes.Pending,
                UserId = r.UserId, UserRole = r.UserRole, ConfigVersion = r.ConfigVersion, ReservedUsd = r.ReservedUsd, CreatedAt = r.Now, ExpiresAt = r.ExpiresAt,
            });
            user.Requests++;
            global.Requests++;
            user.UpdatedAt = global.UpdatedAt = r.Now;
            foreach (var p in new[] { day, monthly })
            {
                p.ReservedUsd += r.ReservedUsd;
                p.Requests++;
                p.UpdatedAt = r.Now;
            }
            // A concurrent duplicate of the same client message id fails here on ux_rag_usage_reservation_client_message (nothing written).
            await SaveChangesAsync(token);
            return (new AiAdmission(id, null), true);
        }, cancellationToken);
    }

    public Task<bool> SettleChatAsync(Guid reservationId, long inputTokens, long outputTokens, AiChatSettlement s, CancellationToken cancellationToken)
        => ExecuteInTransactionAsync<bool>(async token =>
        {
            var reservation = await Db.RagUsageReservations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == reservationId, token);
            var entry = await Db.AiUsageLedger.AsNoTracking().FirstOrDefaultAsync(x => x.Id == reservationId, token);
            if (reservation is null && entry is null) return (false, false);
            if (reservation is not null)
            {
                var (user, global) = await LockLedgerAsync(reservation.UserId, reservation.UsageDate, s.Now, token);
                var tracked = await Db.RagUsageReservations.FirstOrDefaultAsync(x => x.Id == reservationId, token);
                if (tracked is not null)
                {
                    Db.RagUsageReservations.Remove(tracked);
                    user.InputTokens += Math.Max(0, inputTokens);
                    user.OutputTokens += Math.Max(0, outputTokens);
                    global.InputTokens += Math.Max(0, inputTokens);
                    global.OutputTokens += Math.Max(0, outputTokens);
                    user.UpdatedAt = global.UpdatedAt = s.Now;
                }
            }
            if (entry is not null)
            {
                var day = DateOnly.FromDateTime(entry.CreatedAt.UtcDateTime);
                var periods = await LockPeriodsAsync([(AiBudgetPeriodKinds.Day, day), (AiBudgetPeriodKinds.Month, MonthOf(day))], s.Now, token);
                var tracked = await Db.AiUsageLedger.FirstOrDefaultAsync(x => x.Id == reservationId && x.Status == AiUsageStatuses.Reserved, token);
                if (tracked is not null)
                {
                    tracked.Outcome = s.Outcome;
                    tracked.ChatMessageId = s.ChatMessageId;
                    tracked.EmbeddingProvider = s.EmbeddingProvider;
                    tracked.EmbeddingModel = s.EmbeddingModel;
                    tracked.EmbeddingTokens = Math.Max(0, s.EmbeddingTokens);
                    tracked.EmbeddingCostUsd = Math.Max(0, s.EmbeddingCostUsd);
                    tracked.GenerationProvider = s.GenerationProvider;
                    tracked.GenerationModel = s.GenerationModel;
                    tracked.InputTokens = Math.Max(0, s.InputTokens);
                    tracked.CachedInputTokens = Math.Clamp(s.CachedInputTokens, 0, Math.Max(0, s.InputTokens));
                    tracked.OutputTokens = Math.Max(0, s.OutputTokens);
                    tracked.ReasoningTokens = Math.Max(0, s.ReasoningTokens);
                    tracked.GenerationCostUsd = Math.Max(0, s.GenerationCostUsd);
                    tracked.UsageReported = s.UsageReported;
                    tracked.LatencyMs = s.LatencyMs;
                    tracked.SettledAt = s.Now;
                    // Uncertain: the provider may have charged a call it did not report; the reservation is kept as the charge until reviewed.
                    var known = Math.Max(0, s.CostUsd);
                    tracked.Status = s.Uncertain ? AiUsageStatuses.Uncertain : known > 0 ? AiUsageStatuses.Settled : AiUsageStatuses.Released;
                    tracked.CostUsd = s.Uncertain ? Math.Max(tracked.ReservedUsd, known) : known;
                    foreach (var p in periods.Values)
                    {
                        p.ReservedUsd = Math.Max(0, p.ReservedUsd - tracked.ReservedUsd);
                        if (s.Uncertain) p.UncertainUsd += tracked.CostUsd;
                        else p.SettledUsd += known;
                        p.UpdatedAt = s.Now;
                    }
                }
            }
            await SaveChangesAsync(token);
            return (true, true);
        }, cancellationToken);

    public async Task<AiAdmission> TryReserveIngestionAsync(Guid jobId, int attempt, Guid versionId, decimal reservedUsd, decimal monthlyBudgetUsd, int configVersion,
        DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        await ExpireAiReservationsAsync(now, 100, cancellationToken);
        var key = $"ingest:{jobId}:{attempt}";
        try
        {
            return await ExecuteInTransactionAsync<AiAdmission>(async token =>
            {
                var month = MonthOf(DateOnly.FromDateTime(now.UtcDateTime));
                var period = (await LockPeriodsAsync([(AiBudgetPeriodKinds.IngestMonth, month)], now, token))[(AiBudgetPeriodKinds.IngestMonth, month)];
                if (await Db.AiUsageLedger.AsNoTracking().FirstOrDefaultAsync(x => x.IdempotencyKey == key, token) is { } existing)
                    return (new AiAdmission(existing.Id, null), false);   // a retried call of the same attempt
                if (reservedUsd > 0 && period.CommittedUsd + reservedUsd > monthlyBudgetUsd)
                {
                    period.Blocked++;
                    period.UpdatedAt = now;
                    await SaveChangesAsync(token);
                    return (new AiAdmission(null, AiCostErrorCodes.BudgetExceeded,
                        new DateTimeOffset(month.AddMonths(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) - now), true);
                }
                var id = Guid.NewGuid();
                Db.AiUsageLedger.Add(new AiUsageLedgerEntry
                {
                    Id = id, IdempotencyKey = key, Operation = AiUsageOperations.Ingestion, Status = AiUsageStatuses.Reserved, Outcome = AiUsageOutcomes.Pending,
                    IngestionJobId = jobId, VersionId = versionId, ConfigVersion = configVersion, ReservedUsd = reservedUsd, CreatedAt = now, ExpiresAt = expiresAt,
                });
                period.ReservedUsd += reservedUsd;
                period.Requests++;
                period.UpdatedAt = now;
                await SaveChangesAsync(token);
                return (new AiAdmission(id, null), true);
            }, cancellationToken);
        }
        catch (Modules.Common.Persistence.PersistenceConflictException)
        {
            var existing = await Db.AiUsageLedger.AsNoTracking().FirstOrDefaultAsync(x => x.IdempotencyKey == key, cancellationToken);
            return existing is null ? new AiAdmission(null, AiCostErrorCodes.RequestInProgress, TimeSpan.FromSeconds(30)) : new AiAdmission(existing.Id, null);
        }
    }

    public Task<bool> SettleIngestionAsync(Guid ledgerId, string outcome, bool uncertain, string? provider, string? model, long tokens, decimal costUsd,
        DateTimeOffset now, CancellationToken cancellationToken)
        => ExecuteInTransactionAsync<bool>(async token =>
        {
            var entry = await Db.AiUsageLedger.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ledgerId, token);
            if (entry is null) return (false, false);
            var month = MonthOf(DateOnly.FromDateTime(entry.CreatedAt.UtcDateTime));
            var period = (await LockPeriodsAsync([(AiBudgetPeriodKinds.IngestMonth, month)], now, token))[(AiBudgetPeriodKinds.IngestMonth, month)];
            var tracked = await Db.AiUsageLedger.FirstOrDefaultAsync(x => x.Id == ledgerId && x.Status == AiUsageStatuses.Reserved, token);
            if (tracked is null) return (false, false);   // already settled (idempotent) or expired to UNCERTAIN
            var known = Math.Max(0, costUsd);
            tracked.Outcome = outcome;
            tracked.EmbeddingProvider = provider;
            tracked.EmbeddingModel = model;
            tracked.EmbeddingTokens = Math.Max(0, tokens);
            tracked.EmbeddingCostUsd = known;
            tracked.SettledAt = now;
            tracked.Status = uncertain ? AiUsageStatuses.Uncertain : known > 0 ? AiUsageStatuses.Settled : AiUsageStatuses.Released;
            tracked.CostUsd = uncertain ? Math.Max(tracked.ReservedUsd, known) : known;
            period.ReservedUsd = Math.Max(0, period.ReservedUsd - tracked.ReservedUsd);
            if (uncertain) period.UncertainUsd += tracked.CostUsd;
            else period.SettledUsd += known;
            period.UpdatedAt = now;
            await SaveChangesAsync(token);
            return (true, true);
        }, cancellationToken);

    public async Task<int> ExpireAiReservationsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        var due = await Db.AiUsageLedger.AsNoTracking().Where(x => x.Status == AiUsageStatuses.Reserved && x.ExpiresAt <= now)
            .Take(limit).Select(x => new { x.Id, x.Operation, x.CreatedAt }).ToListAsync(cancellationToken);
        if (due.Count == 0) return 0;
        return await ExecuteInTransactionAsync<int>(async token =>
        {
            var keys = due.SelectMany(d => PeriodsOf(d.Operation, d.CreatedAt)).Distinct().ToList();
            var periods = await LockPeriodsAsync(keys, now, token);
            var ids = due.Select(d => d.Id).ToList();
            var rows = await Db.AiUsageLedger.Where(x => ids.Contains(x.Id) && x.Status == AiUsageStatuses.Reserved).ToListAsync(token);
            foreach (var row in rows)
            {
                row.Status = AiUsageStatuses.Uncertain;
                row.Outcome = AiUsageOutcomes.Expired;
                row.CostUsd = row.ReservedUsd;
                row.UsageReported = false;
                row.SettledAt = now;
                foreach (var key in PeriodsOf(row.Operation, row.CreatedAt))
                {
                    var p = periods[key];
                    p.ReservedUsd = Math.Max(0, p.ReservedUsd - row.ReservedUsd);
                    p.UncertainUsd += row.ReservedUsd;
                    p.UpdatedAt = now;
                }
            }
            await SaveChangesAsync(token);
            return (rows.Count, rows.Count > 0);
        }, cancellationToken);
    }

    public Task<bool> ReviewUncertainAsync(Guid ledgerId, decimal costUsd, Guid adminUserId, string note, DateTimeOffset now, CancellationToken cancellationToken)
        => ExecuteInTransactionAsync<bool>(async token =>
        {
            var entry = await Db.AiUsageLedger.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ledgerId, token);
            if (entry is null || entry.Status != AiUsageStatuses.Uncertain) return (false, false);
            var periods = await LockPeriodsAsync(PeriodsOf(entry.Operation, entry.CreatedAt).ToList(), now, token);
            var tracked = await Db.AiUsageLedger.FirstOrDefaultAsync(x => x.Id == ledgerId && x.Status == AiUsageStatuses.Uncertain, token);
            if (tracked is null) return (false, false);
            var actual = AiCostCalculator.Round(Math.Max(0, costUsd));
            foreach (var p in periods.Values)
            {
                p.UncertainUsd = Math.Max(0, p.UncertainUsd - tracked.CostUsd);
                p.SettledUsd += actual;
                p.UpdatedAt = now;
            }
            tracked.Status = actual > 0 ? AiUsageStatuses.Settled : AiUsageStatuses.Released;
            tracked.Outcome = AiUsageOutcomes.Reviewed;
            tracked.CostUsd = actual;
            tracked.ReviewedByUserId = adminUserId;
            tracked.ReviewedAt = now;
            tracked.ReviewNote = note.Length <= 500 ? note : note[..500];
            await SaveChangesAsync(token);
            return (true, true);
        }, cancellationToken);

    public async Task<IReadOnlyList<AiBudgetPeriod>> GetBudgetPeriodsAsync(IReadOnlyCollection<(string Kind, DateOnly Start)> periods, CancellationToken cancellationToken)
    {
        var kinds = periods.Select(p => p.Kind).Distinct().ToList();
        var starts = periods.Select(p => p.Start).Distinct().ToList();
        var rows = await Db.AiBudgetPeriods.AsNoTracking().Where(p => kinds.Contains(p.PeriodKind) && starts.Contains(p.PeriodStart)).ToListAsync(cancellationToken);
        return periods.Select(k => rows.FirstOrDefault(r => r.PeriodKind == k.Kind && r.PeriodStart == k.Start)
                                   ?? new AiBudgetPeriod { PeriodKind = k.Kind, PeriodStart = k.Start }).ToList();
    }

    // ------------------------------------------------------------------ internals

    internal static DateOnly MonthOf(DateOnly date) => new(date.Year, date.Month, 1);

    private static IEnumerable<(string Kind, DateOnly Start)> PeriodsOf(string operation, DateTimeOffset createdAt)
    {
        var day = DateOnly.FromDateTime(createdAt.UtcDateTime);
        if (operation == AiUsageOperations.Ingestion) return [(AiBudgetPeriodKinds.IngestMonth, MonthOf(day))];
        return [(AiBudgetPeriodKinds.Day, day), (AiBudgetPeriodKinds.Month, MonthOf(day))];
    }

    /// <summary>Creates the period rows if needed and locks them FOR UPDATE in (kind, start) order; returns them tracked.</summary>
    private async Task<Dictionary<(string Kind, DateOnly Start), AiBudgetPeriod>> LockPeriodsAsync(IReadOnlyCollection<(string Kind, DateOnly Start)> keys,
        DateTimeOffset now, CancellationToken token)
    {
        var ordered = keys.Distinct().OrderBy(k => k.Kind, StringComparer.Ordinal).ThenBy(k => k.Start).ToList();
        if (Db.Database.IsNpgsql())
        {
            foreach (var (kind, start) in ordered)
                await Db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO ai_budget_period (period_kind, period_start, reserved_usd, settled_usd, uncertain_usd, requests, blocked, updated_at) VALUES ({kind}, {start}, 0, 0, 0, 0, 0, {now.ToUniversalTime()}) ON CONFLICT DO NOTHING",
                    token);
            foreach (var (kind, start) in ordered)
                await Db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM ai_budget_period WHERE period_kind = {kind} AND period_start = {start} FOR UPDATE", token);
        }
        else
        {
            // SQLite (tests) serialises writers; create the rows through EF.
            foreach (var (kind, start) in ordered)
                if (!await Db.AiBudgetPeriods.AnyAsync(p => p.PeriodKind == kind && p.PeriodStart == start, token))
                    Db.AiBudgetPeriods.Add(new AiBudgetPeriod { PeriodKind = kind, PeriodStart = start, UpdatedAt = now });
            await Db.SaveChangesAsync(token);
        }
        var result = new Dictionary<(string, DateOnly), AiBudgetPeriod>();
        foreach (var (kind, start) in ordered)
            result[(kind, start)] = await Db.AiBudgetPeriods.SingleAsync(p => p.PeriodKind == kind && p.PeriodStart == start, token);
        return result;
    }
}
