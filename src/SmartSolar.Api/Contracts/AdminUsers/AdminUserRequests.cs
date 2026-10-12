namespace SmartSolar.Api.Contracts.AdminUsers;

/// <summary>No password field on purpose: the user sets it through the emailed setup link.</summary>
public sealed record CreateAdminUserRequest(string? Email, string? FullName, string? Phone, IReadOnlyList<string>? Roles);

/// <summary>Email is optional; changing it removes the verified flag and sends a new verification link.</summary>
public sealed record UpdateAdminUserRequest(string? FullName, string? Phone, string? Email);

public sealed record ChangeUserStatusRequest(string? Status, string? Reason);

public sealed record ReplaceUserRolesRequest(IReadOnlyList<string>? Roles);
