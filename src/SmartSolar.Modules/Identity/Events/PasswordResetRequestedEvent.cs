namespace SmartSolar.Modules.Identity.Events;

/// <summary>
/// Asks the email delivery consumer to send a password reset link.
/// Carries no password material. ResetUrl contains the raw token and must never be logged.
/// </summary>
public sealed record PasswordResetRequestedEvent(
    Guid UserId,
    string Email,
    string FullName,
    string ResetUrl);
