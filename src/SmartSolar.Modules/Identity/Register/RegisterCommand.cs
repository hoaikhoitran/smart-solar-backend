namespace SmartSolar.Modules.Identity.Register;

public sealed record RegisterCommand(
    string Email,
    string Password,
    string FullName,
    string? Phone);
