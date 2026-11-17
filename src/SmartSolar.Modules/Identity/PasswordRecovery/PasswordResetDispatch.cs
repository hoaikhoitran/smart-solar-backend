using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Email;
using SmartSolar.Modules.Common.Messaging;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Identity.Events;

namespace SmartSolar.Modules.Identity.PasswordRecovery;

/// <summary>
/// Durable record of one one-time-link email to deliver (a transactional outbox row; table <c>password_reset_dispatch</c>). Despite the
/// historical name it carries every link email of the identity module: password reset, email verification and account setup — the kind
/// is the <see cref="Entities.AuthActionToken.Type"/> of <see cref="ActionTokenId"/>, so no extra column is needed. It is written in the
/// same transaction as the token, so a request the API accepted is not lost when the broker or the SMTP server is down: the event is
/// published right after the commit; a failed publish, and a failed SMTP send (the consumer hands the row back), are retried by
/// <c>PasswordResetDispatchWorker</c> with backoff until the token expires. <see cref="Status"/> separates "accepted" (PENDING),
/// "handed to the broker" (PUBLISHED) and "the SMTP server accepted the email" (SENT). <see cref="ResetUrl"/> carries the raw token: it is
/// kept only while the row is PENDING and cleared on publish, send or expiry; never logged.
/// </summary>
public sealed class PasswordResetDispatch
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>The reset token this email carries; deleting the token (a newer request replaces it) deletes the row.</summary>
    public Guid ActionTokenId { get; set; }

    public string Email { get; set; } = null!;
    public string FullName { get; set; } = null!;
    public string? ResetUrl { get; set; }
    public string Status { get; set; } = null!;

    /// <summary>Publish attempts so far.</summary>
    public int PublishAttempts { get; set; }

    /// <summary>Earliest time the next publish attempt may claim the row (also the lease of an attempt in progress).</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>Exception type of the last failed publish or send (never a message that could contain the URL).</summary>
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The token's expiry: nothing is published after it.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>A claimed row and the kind of link it carries.</summary>
public sealed record ClaimedDispatch(PasswordResetDispatch Row, AuthActionTokenType Kind);

public static class AuthLinkDispatches
{
    /// <summary>
    /// A new PENDING row due at once (<see cref="DateTimeOffset.UnixEpoch"/>: whatever clock the dispatcher uses, it is due), to be saved
    /// in the same transaction as its token.
    /// </summary>
    public static PasswordResetDispatch Pending(Guid userId, Guid actionTokenId, string email, string fullName, string url, DateTimeOffset now, DateTimeOffset expiresAt)
        => new()
        {
            Id = Guid.NewGuid(), UserId = userId, ActionTokenId = actionTokenId, Email = email, FullName = fullName, ResetUrl = url,
            Status = PasswordResetDispatchStatuses.Pending, NextAttemptAt = DateTimeOffset.UnixEpoch, CreatedAt = now, ExpiresAt = expiresAt,
        };
}

public static class PasswordResetDispatchStatuses
{
    public const string Pending = "PENDING";
    public const string Published = "PUBLISHED";
    public const string Sent = "SENT";
    public const string Expired = "EXPIRED";
    public static readonly IReadOnlyList<string> All = [Pending, Published, Sent, Expired];
}

public interface IPasswordResetDispatchStore
{
    /// <summary>
    /// Atomically claims a due PENDING row (conditional update: only one caller, on any instance, wins) and pushes its
    /// <see cref="PasswordResetDispatch.NextAttemptAt"/> by <paramref name="lease"/>. Null when another caller holds it or it is not due.
    /// </summary>
    Task<ClaimedDispatch?> TryClaimAsync(Guid id, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>
    /// The SMTP send of a PUBLISHED row failed: put it back to PENDING with its URL (from the message) and the next attempt after the
    /// publish backoff, so the worker re-publishes it until the token expires. False when the row is gone, SENT or EXPIRED.
    /// </summary>
    Task<bool> ReturnForRetryAsync(Guid id, string url, DateTimeOffset now, string error, CancellationToken cancellationToken);

    /// <summary>Due, unexpired PENDING rows (oldest first).</summary>
    Task<IReadOnlyList<Guid>> ListDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    Task MarkPublishedAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken);
    Task RecordPublishFailureAsync(Guid id, DateTimeOffset nextAttemptAt, string error, CancellationToken cancellationToken);

    /// <summary>PENDING rows whose token expired become EXPIRED and lose their URL; returns how many.</summary>
    Task<int> ExpireAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Status of the row, or null when it no longer exists (token replaced or deleted).</summary>
    Task<string?> GetStatusAsync(Guid id, CancellationToken cancellationToken);

    Task MarkSentAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken);
    Task RecordSendFailureAsync(Guid id, string error, CancellationToken cancellationToken);
}

/// <summary>Publishes outbox rows. Shared by the request (immediate attempt) and the worker (retries); the claim makes them exclusive.</summary>
public sealed class PasswordResetDispatcher(IPasswordResetDispatchStore store, IIntegrationEventPublisher publisher, IEmailDeliveryStatus delivery,
    TimeProvider time, ILogger<PasswordResetDispatcher> logger)
{
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    /// <summary>Delay before the attempt after <paramref name="attempts"/> failures: 15 s, 30 s, 60 s … capped at 5 minutes.</summary>
    public static TimeSpan Backoff(int attempts) => TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, 15 * Math.Pow(2, Math.Max(0, attempts - 1))));

    /// <summary>
    /// One publish attempt. Returns true when the event reached the broker. Never throws for broker errors: the row stays PENDING and
    /// is retried. Skipped while email delivery is not configured (no broker / SMTP): the row then simply expires with its token.
    /// </summary>
    public async Task<bool> TryPublishAsync(Guid dispatchId, CancellationToken cancellationToken)
    {
        if (!delivery.CanDeliver) return false;
        var now = time.GetUtcNow();
        var claimed = await store.TryClaimAsync(dispatchId, now, Lease, cancellationToken);
        if (claimed?.Row.ResetUrl is not { } url) return false;
        var row = claimed.Row;
        try
        {
            switch (claimed.Kind)
            {
                case AuthActionTokenType.EmailVerification:
                    await publisher.PublishAsync(new EmailVerificationRequestedEvent(row.UserId, row.Email, row.FullName, url, row.Id), cancellationToken);
                    break;
                case AuthActionTokenType.AccountSetup:
                    await publisher.PublishAsync(new AccountSetupRequestedEvent(row.UserId, row.Email, row.FullName, url, row.Id), cancellationToken);
                    break;
                default:
                    await publisher.PublishAsync(new PasswordResetRequestedEvent(row.UserId, row.Email, row.FullName, url, row.Id), cancellationToken);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The URL carries the raw token: only the user and the exception type are logged.
            logger.LogWarning("{Kind} email for user {UserId} could not be published ({Error}); attempt {Attempt} will be retried.",
                claimed.Kind, row.UserId, ex.GetType().Name, row.PublishAttempts);
            await store.RecordPublishFailureAsync(row.Id, now + Backoff(row.PublishAttempts), ex.GetType().Name, CancellationToken.None);
            return false;
        }
        await store.MarkPublishedAsync(row.Id, time.GetUtcNow(), CancellationToken.None);
        return true;
    }

    /// <summary>Worker pass: expire stale rows, then retry the due ones.</summary>
    public async Task<(int Published, int Expired)> SweepAsync(int limit, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var expired = await store.ExpireAsync(now, cancellationToken);
        if (expired > 0) logger.LogWarning("{Count} one-time link email(s) expired before they could be delivered.", expired);
        var published = 0;
        if (!delivery.CanDeliver) return (0, expired);
        foreach (var id in await store.ListDueAsync(now, limit, cancellationToken))
            if (await TryPublishAsync(id, cancellationToken)) published++;
        return (published, expired);
    }
}
