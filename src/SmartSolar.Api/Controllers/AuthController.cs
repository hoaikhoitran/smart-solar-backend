using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Contracts.Auth;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.EmailVerification;
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

    public AuthController(
        RegisterHandler registerHandler,
        VerifyEmailHandler verifyEmailHandler,
        ResendVerificationHandler resendVerificationHandler,
        IValidator<RegisterCommand> registerValidator,
        IValidator<VerifyEmailCommand> verifyEmailValidator,
        IValidator<ResendVerificationCommand> resendValidator)
    {
        _registerHandler = registerHandler;
        _verifyEmailHandler = verifyEmailHandler;
        _resendVerificationHandler = resendVerificationHandler;
        _registerValidator = registerValidator;
        _verifyEmailValidator = verifyEmailValidator;
        _resendValidator = resendValidator;
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
