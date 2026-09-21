using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Contracts.Auth;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.EmailVerification;
using SmartSolar.Modules.Identity.Login;
using SmartSolar.Modules.Identity.PasswordRecovery;
using SmartSolar.Modules.Identity.RefreshTokens;
using SmartSolar.Modules.Identity.Register;

namespace SmartSolar.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly RegisterHandler _registerHandler;
    private readonly VerifyEmailHandler _verifyEmailHandler;
    private readonly ResendVerificationHandler _resendVerificationHandler;
    private readonly IValidator<RegisterCommand> _registerValidator;
    private readonly IValidator<VerifyEmailCommand> _verifyEmailValidator;
    private readonly IValidator<ResendVerificationCommand> _resendValidator;
    private readonly LoginHandler _loginHandler;
    private readonly RefreshTokenHandler _refreshTokenHandler;
    private readonly LogoutHandler _logoutHandler;
    private readonly ForgotPasswordHandler _forgotPasswordHandler;
    private readonly ResetPasswordHandler _resetPasswordHandler;
    private readonly ChangePasswordHandler _changePasswordHandler;
    private readonly IValidator<LoginCommand> _loginValidator;
    private readonly IValidator<RefreshTokenCommand> _refreshValidator;
    private readonly IValidator<LogoutCommand> _logoutValidator;
    private readonly IValidator<ForgotPasswordCommand> _forgotPasswordValidator;
    private readonly IValidator<ResetPasswordCommand> _resetPasswordValidator;
    private readonly IValidator<ChangePasswordCommand> _changePasswordValidator;

    public AuthController(
        RegisterHandler registerHandler,
        VerifyEmailHandler verifyEmailHandler,
        ResendVerificationHandler resendVerificationHandler,
        IValidator<RegisterCommand> registerValidator,
        IValidator<VerifyEmailCommand> verifyEmailValidator,
        IValidator<ResendVerificationCommand> resendValidator,
        LoginHandler loginHandler,
        RefreshTokenHandler refreshTokenHandler,
        LogoutHandler logoutHandler,
        ForgotPasswordHandler forgotPasswordHandler,
        ResetPasswordHandler resetPasswordHandler,
        ChangePasswordHandler changePasswordHandler,
        IValidator<LoginCommand> loginValidator,
        IValidator<RefreshTokenCommand> refreshValidator,
        IValidator<LogoutCommand> logoutValidator,
        IValidator<ForgotPasswordCommand> forgotPasswordValidator,
        IValidator<ResetPasswordCommand> resetPasswordValidator,
        IValidator<ChangePasswordCommand> changePasswordValidator)
    {
        _registerHandler = registerHandler;
        _verifyEmailHandler = verifyEmailHandler;
        _resendVerificationHandler = resendVerificationHandler;
        _registerValidator = registerValidator;
        _verifyEmailValidator = verifyEmailValidator;
        _resendValidator = resendValidator;
        _loginHandler = loginHandler;
        _refreshTokenHandler = refreshTokenHandler;
        _logoutHandler = logoutHandler;
        _forgotPasswordHandler = forgotPasswordHandler;
        _resetPasswordHandler = resetPasswordHandler;
        _changePasswordHandler = changePasswordHandler;
        _loginValidator = loginValidator;
        _refreshValidator = refreshValidator;
        _logoutValidator = logoutValidator;
        _forgotPasswordValidator = forgotPasswordValidator;
        _resetPasswordValidator = resetPasswordValidator;
        _changePasswordValidator = changePasswordValidator;
    }

    [HttpPost("register")]
    [EnableRateLimiting(RateLimitingExtensions.RegisterPolicy)]
    [ProducesResponseType(typeof(ApiResponse<RegisterResponse>), StatusCodes.Status201Created)]
    public async Task<IActionResult> Register(
        [FromBody] RegisterRequest request,
        CancellationToken cancellationToken)
    {
        var command = new RegisterCommand(
            request.Email ?? string.Empty,
            request.Password ?? string.Empty,
            request.FullName ?? string.Empty,
            request.Phone);

        if (await Validate(_registerValidator, command, cancellationToken) is { } validationError)
        {
            return validationError;
        }

        var result = await _registerHandler.HandleAsync(command, cancellationToken);

        if (result.Outcome == RegisterOutcome.DuplicateEmail)
        {
            return Conflict(Failure(
                AuthErrorCodes.EmailAlreadyExists,
                "An account with this email already exists."));
        }

        return StatusCode(
            StatusCodes.Status201Created,
            Success(new RegisterResponse(result.UserId, result.Email, VerificationRequired: true)));
    }

    [HttpPost("verify-email")]
    [EnableRateLimiting(RateLimitingExtensions.VerifyEmailPolicy)]
    [ProducesResponseType(typeof(ApiResponse<VerifyEmailResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> VerifyEmail(
        [FromBody] VerifyEmailRequest request,
        CancellationToken cancellationToken)
    {
        var command = new VerifyEmailCommand(request.Token ?? string.Empty);

        if (await Validate(_verifyEmailValidator, command, cancellationToken) is { } validationError)
        {
            return validationError;
        }

        var result = await _verifyEmailHandler.HandleAsync(command, cancellationToken);

        if (result.Outcome == VerifyEmailOutcome.InvalidOrExpired)
        {
            // One message for unknown, expired and used tokens alike.
            return BadRequest(Failure(
                AuthErrorCodes.VerificationTokenInvalidOrExpired,
                "This verification link is invalid or has expired."));
        }

        return Ok(Success(new VerifyEmailResponse(EmailVerified: true)));
    }

    [HttpPost("resend-verification")]
    [EnableRateLimiting(RateLimitingExtensions.ResendVerificationPolicy)]
    [ProducesResponseType(typeof(ApiResponse<ResendVerificationResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ResendVerification(
        [FromBody] ResendVerificationRequest request,
        CancellationToken cancellationToken)
    {
        var command = new ResendVerificationCommand(request.Email ?? string.Empty);

        if (await Validate(_resendValidator, command, cancellationToken) is { } validationError)
        {
            return validationError;
        }

        var result = await _resendVerificationHandler.HandleAsync(command, cancellationToken);

        // Identical body regardless of whether an email was actually sent.
        return Ok(Success(new ResendVerificationResponse(result.Accepted)));
    }

    [HttpPost("login")]
    [EnableRateLimiting(RateLimitingExtensions.LoginPolicy)]
    [ProducesResponseType(typeof(ApiResponse<AuthTokensResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var command = new LoginCommand(request.Email ?? string.Empty, request.Password ?? string.Empty);

        if (await Validate(_loginValidator, command, cancellationToken) is { } validationError)
        {
            return validationError;
        }

        var result = await _loginHandler.HandleAsync(command, cancellationToken);

        if (result.Outcome == LoginOutcome.InvalidCredentials)
        {
            // One message for unknown email, wrong password and inactive accounts.
            return Unauthorized(Failure(
                AuthErrorCodes.InvalidCredentials,
                "Email or password is incorrect, or the account cannot sign in."));
        }

        return Ok(Success(new AuthTokensResponse(
            result.UserId,
            result.AccessToken,
            result.AccessTokenExpiresAt,
            result.RefreshToken,
            result.RefreshTokenExpiresAt)));
    }

    [HttpPost("refresh")]
    [EnableRateLimiting(RateLimitingExtensions.RefreshPolicy)]
    [ProducesResponseType(typeof(ApiResponse<AuthTokensResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Refresh(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        var command = new RefreshTokenCommand(request.RefreshToken ?? string.Empty);

        if (await Validate(_refreshValidator, command, cancellationToken) is { } validationError)
        {
            return validationError;
        }

        var result = await _refreshTokenHandler.HandleAsync(command, cancellationToken);

        if (result.Outcome == RefreshOutcome.Invalid)
        {
            return Unauthorized(Failure(
                AuthErrorCodes.RefreshTokenInvalid,
                "This refresh token is invalid or has expired."));
        }

        return Ok(Success(new AuthTokensResponse(
            result.UserId,
            result.AccessToken,
            result.AccessTokenExpiresAt,
            result.RefreshToken,
            result.RefreshTokenExpiresAt)));
    }

    [HttpPost("logout")]
    [EnableRateLimiting(RateLimitingExtensions.RefreshPolicy)]
    [ProducesResponseType(typeof(ApiResponse<LogoutResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Logout(
        [FromBody] LogoutRequest request,
        CancellationToken cancellationToken)
    {
        var command = new LogoutCommand(request.RefreshToken ?? string.Empty);

        if (await Validate(_logoutValidator, command, cancellationToken) is { } validationError)
        {
            return validationError;
        }

        var result = await _logoutHandler.HandleAsync(command, cancellationToken);

        // Identical body whether or not the token existed.
        return Ok(Success(new LogoutResponse(result.Accepted)));
    }

    [HttpPost("forgot-password")]
    [EnableRateLimiting(RateLimitingExtensions.ForgotPasswordPolicy)]
    [ProducesResponseType(typeof(ApiResponse<ForgotPasswordResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ForgotPassword(
        [FromBody] ForgotPasswordRequest request,
        CancellationToken cancellationToken)
    {
        var command = new ForgotPasswordCommand(request.Email ?? string.Empty);

        if (await Validate(_forgotPasswordValidator, command, cancellationToken) is { } validationError)
        {
            return validationError;
        }

        var result = await _forgotPasswordHandler.HandleAsync(command, cancellationToken);

        // Identical body for every account state.
        return Ok(Success(new ForgotPasswordResponse(result.Accepted)));
    }

    [HttpPost("reset-password")]
    [EnableRateLimiting(RateLimitingExtensions.ResetPasswordPolicy)]
    [ProducesResponseType(typeof(ApiResponse<PasswordChangedResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ResetPassword(
        [FromBody] ResetPasswordRequest request,
        CancellationToken cancellationToken)
    {
        var command = new ResetPasswordCommand(
            request.Token ?? string.Empty,
            request.NewPassword ?? string.Empty);

        if (await Validate(_resetPasswordValidator, command, cancellationToken) is { } validationError)
        {
            return validationError;
        }

        var result = await _resetPasswordHandler.HandleAsync(command, cancellationToken);

        if (result.Outcome == ResetPasswordOutcome.InvalidOrExpiredToken)
        {
            // One message for unknown, used and expired tokens.
            return BadRequest(Failure(
                AuthErrorCodes.PasswordResetTokenInvalidOrExpired,
                "This password reset link is invalid or has expired."));
        }

        return Ok(Success(new PasswordChangedResponse(PasswordChanged: true)));
    }

    [Authorize]
    [HttpPost("change-password")]
    [EnableRateLimiting(RateLimitingExtensions.ChangePasswordPolicy)]
    [ProducesResponseType(typeof(ApiResponse<PasswordChangedResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        // The account comes from the token, never from the request body.
        if (GetAuthenticatedUserId() is not { } userId)
        {
            return Unauthorized(Failure(
                AuthErrorCodes.Unauthorized,
                "Authentication is required for this request."));
        }

        var command = new ChangePasswordCommand(
            userId,
            request.CurrentPassword ?? string.Empty,
            request.NewPassword ?? string.Empty);

        if (await Validate(_changePasswordValidator, command, cancellationToken) is { } validationError)
        {
            return validationError;
        }

        var result = await _changePasswordHandler.HandleAsync(command, cancellationToken);

        return result.Outcome switch
        {
            ChangePasswordOutcome.InvalidCurrentPassword => BadRequest(Failure(
                AuthErrorCodes.CurrentPasswordInvalid,
                "The current password is incorrect.")),
            ChangePasswordOutcome.AccountNotActive => Unauthorized(Failure(
                AuthErrorCodes.AccountNotActive,
                "This account cannot change its password.")),
            _ => Ok(Success(new PasswordChangedResponse(PasswordChanged: true)))
        };
    }

    private Guid? GetAuthenticatedUserId()
    {
        var subject = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

        return Guid.TryParse(subject, out var userId) ? userId : null;
    }

    private async Task<IActionResult?> Validate<T>(
        IValidator<T> validator,
        T command,
        CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(command, cancellationToken);

        if (validation.IsValid)
        {
            return null;
        }

        var details = validation.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

        return BadRequest(Failure(
            AuthErrorCodes.ValidationFailed,
            "One or more fields are invalid.",
            details));
    }

    private ApiResponse<TData> Success<TData>(TData data)
        => ApiResponse<TData>.Success(data, HttpContext.GetTraceId());

    private ApiResponse<object> Failure(string code, string message, object? details = null)
        => ApiResponse<object>.Failure(new ApiError(code, message, details), HttpContext.GetTraceId());
}
