namespace SmartSolar.Modules.Identity.Constants;

public static class AuthErrorCodes
{
    public const string ValidationFailed = "AUTH_VALIDATION_FAILED";
    public const string EmailAlreadyExists = "AUTH_EMAIL_ALREADY_EXISTS";
    public const string VerificationTokenInvalidOrExpired = "AUTH_VERIFICATION_TOKEN_INVALID_OR_EXPIRED";
    public const string TooManyRequests = "AUTH_TOO_MANY_REQUESTS";
    public const string InternalError = "INTERNAL_ERROR";
}
