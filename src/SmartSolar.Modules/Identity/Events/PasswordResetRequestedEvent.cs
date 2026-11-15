namespace SmartSolar.Modules.Identity.Events;

/// <summary>
/// Asks the email delivery consumer to send a password reset link.
/// Carries no password material. ResetUrl contains the raw token and must never be logged.
/// <see cref="DispatchId"/> identifies the outbox row (<c>password_reset_dispatch</c>): the consumer uses it to skip a redelivered
/// message whose email was already sent and to record the outcome. Null only for messages published before the outbox existed.
/// </summary>
public sealed record PasswordResetRequestedEvent(
    Guid UserId,
    string Email,
    string FullName,
    string ResetUrl,
    Guid? DispatchId = null);
