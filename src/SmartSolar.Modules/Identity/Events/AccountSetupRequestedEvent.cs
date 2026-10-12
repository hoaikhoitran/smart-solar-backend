namespace SmartSolar.Modules.Identity.Events;

/// <summary>
/// Asks the email consumer to send the account-setup link of an admin-provisioned account.
/// SetupUrl contains the raw one-time token and must never be logged.
/// </summary>
public sealed record AccountSetupRequestedEvent(
    Guid UserId,
    string Email,
    string FullName,
    string SetupUrl);
