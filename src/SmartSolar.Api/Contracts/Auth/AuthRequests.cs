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

public sealed record LoginRequest(string? Email, string? Password);

public sealed record RefreshTokenRequest(string? RefreshToken);

public sealed record LogoutRequest(string? RefreshToken);

public sealed record ForgotPasswordRequest(string? Email);

public sealed record ResetPasswordRequest(string? Token, string? NewPassword);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record AuthTokensResponse(
    Guid UserId,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    string TokenType = "Bearer");

public sealed record LogoutResponse(bool Accepted);

public sealed record ForgotPasswordResponse(bool Accepted);

public sealed record PasswordChangedResponse(bool PasswordChanged);
