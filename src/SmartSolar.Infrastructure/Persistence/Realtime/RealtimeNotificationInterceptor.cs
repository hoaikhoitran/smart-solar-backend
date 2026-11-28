using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Common.Realtime;
using SmartSolar.Modules.Scheduling.Dispatch;
using SmartSolar.Modules.Scheduling.Entities;

namespace SmartSolar.Infrastructure.Persistence.Realtime;

/// <summary>
/// Post-commit realtime hook (2026-11-28; registered only when <c>Realtime:Enabled</c>). It observes the notification rows the business code already
/// adds — <see cref="AfterSalesNotification"/>, <see cref="SchedulingNotification"/>, <see cref="DispatchNotification"/> — and the read marks of in-app
/// notifications, and hands them to <see cref="IRealtimeNotificationPublisher"/> only once the database has committed them:
/// <list type="bullet">
/// <item>SaveChanges inside an explicit transaction (every <c>ExecuteInTransactionAsync</c>): the rows wait for <c>TransactionCommitted</c>; a
/// rollback, a failed commit, a failed SaveChanges or a new transaction (execution-strategy retry) discards them.</item>
/// <item>SaveChanges without a transaction (EF's own implicit transaction): published after <c>SavedChanges</c>, i.e. after that commit.</item>
/// </list>
/// Nothing in the business code, the entities, the model or the schema changes. A publication failure or timeout is logged and swallowed: it can
/// never fail, delay beyond the timeout or roll back the business operation. One singleton instance; the pending rows are kept per DbContext.
/// </summary>
public sealed class RealtimeNotificationInterceptor(IRealtimeNotificationPublisher publisher, RealtimeOptions options,
    ILogger<RealtimeNotificationInterceptor> logger) : ISaveChangesInterceptor, IDbTransactionInterceptor
{
    private sealed class State
    {
        public List<RealtimeNotification> Staged { get; } = [];
        public List<RealtimeNotification> Pending { get; } = [];
    }

    private readonly ConditionalWeakTable<DbContext, State> _states = new();

    private State For(DbContext context) => _states.GetValue(context, _ => new State());

    // ------------------------------------------------------------------ SaveChanges

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stage(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stage(eventData.Context);
        return result;
    }

    public async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not { } context) return result;
        var state = For(context);
        if (state.Staged.Count == 0) return result;
        var staged = state.Staged.ToList();
        state.Staged.Clear();
        if (context.Database.CurrentTransaction is null)
            await PublishAsync(staged);   // EF's implicit transaction has committed
        else
            state.Pending.AddRange(staged);   // wait for the caller's commit
        return result;
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is not { } context) return result;
        var state = For(context);
        if (state.Staged.Count == 0) return result;
        var staged = state.Staged.ToList();
        state.Staged.Clear();
        if (context.Database.CurrentTransaction is null) PublishAsync(staged).GetAwaiter().GetResult();
        else state.Pending.AddRange(staged);
        return result;
    }

    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context, staged: true, pending: false);
        return Task.CompletedTask;
    }

    public void SaveChangesFailed(DbContextErrorEventData eventData) => Discard(eventData.Context, staged: true, pending: false);

    public Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context, staged: true, pending: false);
        return Task.CompletedTask;
    }

    public void SaveChangesCanceled(DbContextEventData eventData) => Discard(eventData.Context, staged: true, pending: false);

    // ------------------------------------------------------------------ transactions

    public ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result,
        CancellationToken cancellationToken = default)
    {
        // A new transaction on this context (first attempt or an execution-strategy retry): nothing from an earlier attempt may survive.
        Discard(eventData.Context, staged: false, pending: true);
        return ValueTask.FromResult(result);
    }

    public DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        Discard(eventData.Context, staged: false, pending: true);
        return result;
    }

    public async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (TakePending(eventData.Context) is { Count: > 0 } committed) await PublishAsync(committed);
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (TakePending(eventData.Context) is { Count: > 0 } committed) PublishAsync(committed).GetAwaiter().GetResult();
    }

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context, staged: true, pending: true);
        return Task.CompletedTask;
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Discard(eventData.Context, staged: true, pending: true);

    public Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context, staged: true, pending: true);
        return Task.CompletedTask;
    }

    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => Discard(eventData.Context, staged: true, pending: true);

    // ------------------------------------------------------------------ helpers

    private void Stage(DbContext? context)
    {
        if (context is null) return;
        var state = For(context);
        state.Staged.Clear();   // a SaveChanges retried after a failure re-stages from the change tracker
        foreach (var entry in context.ChangeTracker.Entries())
            if (Map(entry) is { } notification) state.Staged.Add(notification);
    }

    private List<RealtimeNotification>? TakePending(DbContext? context)
    {
        if (context is null || !_states.TryGetValue(context, out var state) || state.Pending.Count == 0) return null;
        var pending = state.Pending.ToList();
        state.Pending.Clear();
        return pending;
    }

    private void Discard(DbContext? context, bool staged, bool pending)
    {
        if (context is null || !_states.TryGetValue(context, out var state)) return;
        if (staged) state.Staged.Clear();
        if (pending) state.Pending.Clear();
    }

    /// <summary>The recipient and the resource come from the persisted row only (chosen by the business code); never from a client.</summary>
    internal static RealtimeNotification? Map(EntityEntry entry)
    {
        switch (entry.Entity)
        {
            case AfterSalesNotification n when entry.State == EntityState.Added:
                return new RealtimeNotification(n.RecipientUserId, n.Id, RealtimeEventTypes.NotificationCreated, RealtimeSources.InApp, n.Type,
                    n.RequestId is not null ? RealtimeResourceTypes.ServiceRequest
                        : n.SubscriptionId is not null ? RealtimeResourceTypes.ServiceSubscription : RealtimeResourceTypes.Notification,
                    n.RequestId ?? n.SubscriptionId ?? n.Id, n.CreatedAt);
            case AfterSalesNotification n when entry.State == EntityState.Modified && n.ReadAt is not null
                                               && entry.Property(nameof(AfterSalesNotification.ReadAt)) is { IsModified: true, OriginalValue: null }:
                return new RealtimeNotification(n.RecipientUserId, n.Id, RealtimeEventTypes.NotificationRead, RealtimeSources.InApp, n.Type,
                    RealtimeResourceTypes.Notification, n.Id, n.ReadAt.Value);
            case SchedulingNotification n when entry.State == EntityState.Added:
                return new RealtimeNotification(n.RecipientUserId, n.Id, RealtimeEventTypes.NotificationCreated, RealtimeSources.Scheduling, n.Type,
                    RealtimeResourceTypes.Appointment, n.AppointmentId, n.CreatedAt);
            case DispatchNotification n when entry.State == EntityState.Added:
                return new RealtimeNotification(n.RecipientUserId, n.Id, RealtimeEventTypes.NotificationCreated, RealtimeSources.Dispatch, n.Type,
                    RealtimeResourceTypes.DispatchRequest, n.DispatchRequestId, n.CreatedAt);
            default:
                return null;
        }
    }

    private async Task PublishAsync(List<RealtimeNotification> committed)
    {
        var batch = committed.Count <= options.MaxEventsPerCommit ? committed : committed.Take(options.MaxEventsPerCommit).ToList();
        if (batch.Count < committed.Count)
            logger.LogWarning("Realtime: {Dropped} of {Total} committed notification events were not published (Realtime:MaxEventsPerCommit).",
                committed.Count - batch.Count, committed.Count);
        // Own token: an aborted HTTP request must not cancel the delivery of what it already committed; bounded by the timeout.
        using var timeout = new CancellationTokenSource(options.PublishTimeoutMilliseconds);
        try
        {
            await publisher.PublishAsync(batch, timeout.Token).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Realtime: publication of {Count} committed notification events timed out after {Timeout} ms.", batch.Count,
                options.PublishTimeoutMilliseconds);
        }
        catch (Exception ex)
        {
            // Never surfaces to the business operation (already committed).
            logger.LogWarning(ex, "Realtime: publication of {Count} committed notification events failed.", batch.Count);
        }
    }
}
