namespace SmartSolar.Modules.Identity.Constants;

public static class AuthErrorCodes
{
    public const string ValidationFailed = "AUTH_VALIDATION_FAILED";
    public const string EmailAlreadyExists = "AUTH_EMAIL_ALREADY_EXISTS";
    public const string VerificationTokenInvalidOrExpired = "AUTH_VERIFICATION_TOKEN_INVALID_OR_EXPIRED";
    public const string InvalidCredentials = "AUTH_INVALID_CREDENTIALS";
    public const string AccountNotActive = "AUTH_ACCOUNT_NOT_ACTIVE";
    public const string RefreshTokenInvalid = "AUTH_REFRESH_TOKEN_INVALID";
    public const string PasswordResetTokenInvalidOrExpired = "AUTH_PASSWORD_RESET_TOKEN_INVALID_OR_EXPIRED";
    public const string CurrentPasswordInvalid = "AUTH_CURRENT_PASSWORD_INVALID";
    public const string Unauthorized = "AUTH_UNAUTHORIZED";
    public const string Forbidden = "AUTH_FORBIDDEN";
    public const string TooManyRequests = "AUTH_TOO_MANY_REQUESTS";
    public const string InternalError = "INTERNAL_ERROR";
}
