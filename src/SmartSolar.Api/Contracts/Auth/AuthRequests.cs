namespace SmartSolar.Api.Contracts.Auth;

public sealed record RegisterRequest(
    string? Email,
    string? Password,
    string? FullName,
    string? Phone);

public sealed record VerifyEmailRequest(string? Token);

public sealed record ResendVerificationRequest(string? Email);

public sealed record RegisterResponse(Guid UserId, string Email, bool VerificationRequired);

public sealed record VerifyEmailResponse(bool EmailVerified);

public sealed record ResendVerificationResponse(bool Accepted);
