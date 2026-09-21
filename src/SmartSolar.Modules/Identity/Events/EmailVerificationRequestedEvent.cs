namespace SmartSolar.Modules.Identity.Events;

/// <summary>
/// Asks the email delivery consumer to send a verification link.
/// Carries no password material. VerificationUrl contains the raw token and must never be logged.
/// </summary>
public sealed record EmailVerificationRequestedEvent(
    Guid UserId,
    string Email,
    string FullName,
    string VerificationUrl);
