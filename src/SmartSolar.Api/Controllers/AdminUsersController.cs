using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Contracts.AdminUsers;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.AdminUsers;
using SmartSolar.Modules.Identity.Constants;

namespace SmartSolar.Api.Controllers;

/// <summary>
/// Account administration. ADMIN only (MANAGER has no identity administration), so only an
/// administrator can grant or revoke ADMIN. Responses never contain password hashes, refresh
/// tokens, verification/reset tokens or setup links.
/// </summary>
[ApiController]
[Route("api/admin/users")]
[Authorize(Roles = RoleCodes.Admin)]
public sealed class AdminUsersController(
    AdminUserHandlers handlers,
    IValidator<CreateAdminUserCommand> createValidator,
    IValidator<UpdateAdminUserCommand> updateValidator,
    IValidator<ChangeUserStatusCommand> statusValidator,
    IValidator<ReplaceUserRolesCommand> rolesValidator,
    IValidator<DeleteAdminUserCommand> deleteValidator) : ControllerBase
{
    /// <summary>GET /api/admin/users?page=1&amp;pageSize=20&amp;search=&amp;role=&amp;status=&amp;sort=createdAt|name|status&amp;direction=asc|desc&amp;includeDeleted=false</summary>
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.AdminUsersReadPolicy)]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null, [FromQuery] string? role = null,
        [FromQuery] string? status = null, [FromQuery] string? sort = null, [FromQuery] string? direction = null,
        [FromQuery] bool includeDeleted = false, CancellationToken cancellationToken = default)
        => Respond(await handlers.ListAsync(new ListAdminUsersQuery(page, pageSize, search, role, status, sort, direction, includeDeleted), cancellationToken));

    [HttpGet("{userId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.AdminUsersReadPolicy)]
    public async Task<IActionResult> Get(Guid userId, CancellationToken cancellationToken)
        => Respond(await handlers.GetAsync(userId, cancellationToken));

    [HttpPost]
    [EnableRateLimiting(RateLimitingExtensions.AdminUsersWritePolicy)]
    public async Task<IActionResult> Create([FromBody] CreateAdminUserRequest request, CancellationToken cancellationToken)
    {
        if (ActorId() is not { } actor) return UnauthorizedFailure();
        var command = new CreateAdminUserCommand(actor, request.Email ?? "", request.FullName ?? "", request.Phone, request.Roles ?? []);
        var validation = await createValidator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid) return ValidationFailure(validation);
        return Respond(await handlers.CreateAsync(command, cancellationToken), StatusCodes.Status201Created);
    }

    [HttpPut("{userId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.AdminUsersWritePolicy)]
    public async Task<IActionResult> Update(Guid userId, [FromBody] UpdateAdminUserRequest request, CancellationToken cancellationToken)
    {
        if (ActorId() is not { } actor) return UnauthorizedFailure();
        var command = new UpdateAdminUserCommand(actor, userId, request.FullName ?? "", request.Phone, request.Email);
        var validation = await updateValidator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid) return ValidationFailure(validation);
        return Respond(await handlers.UpdateAsync(command, cancellationToken));
    }

    [HttpPatch("{userId:guid}/status")]
    [EnableRateLimiting(RateLimitingExtensions.AdminUsersWritePolicy)]
    public async Task<IActionResult> ChangeStatus(Guid userId, [FromBody] ChangeUserStatusRequest request, CancellationToken cancellationToken)
    {
        if (ActorId() is not { } actor) return UnauthorizedFailure();
        var command = new ChangeUserStatusCommand(actor, userId, request.Status ?? "", request.Reason);
        var validation = await statusValidator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid) return ValidationFailure(validation);
        return Respond(await handlers.ChangeStatusAsync(command, cancellationToken));
    }

    [HttpPut("{userId:guid}/roles")]
    [EnableRateLimiting(RateLimitingExtensions.AdminUsersWritePolicy)]
    public async Task<IActionResult> ReplaceRoles(Guid userId, [FromBody] ReplaceUserRolesRequest request, CancellationToken cancellationToken)
    {
        if (ActorId() is not { } actor) return UnauthorizedFailure();
        var command = new ReplaceUserRolesCommand(actor, userId, request.Roles ?? []);
        var validation = await rolesValidator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid) return ValidationFailure(validation);
        return Respond(await handlers.ReplaceRolesAsync(command, cancellationToken));
    }

    /// <summary>Soft delete (history and references are kept). Idempotent: 204 also when already deleted.</summary>
    [HttpDelete("{userId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.AdminUsersWritePolicy)]
    public async Task<IActionResult> Delete(Guid userId, [FromQuery] string? reason, CancellationToken cancellationToken)
    {
        if (ActorId() is not { } actor) return UnauthorizedFailure();
        var command = new DeleteAdminUserCommand(actor, userId, reason);
        var validation = await deleteValidator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid) return ValidationFailure(validation);
        var result = await handlers.DeleteAsync(command, cancellationToken);
        return result.Succeeded ? NoContent() : Respond(result);
    }

    /// <summary>Emails a one-time account-setup, verification or password-reset link. The link is never returned.</summary>
    [HttpPost("{userId:guid}/send-password-reset")]
    [EnableRateLimiting(RateLimitingExtensions.AdminUsersRecoveryPolicy)]
    public async Task<IActionResult> SendPasswordReset(Guid userId, CancellationToken cancellationToken)
        => ActorId() is { } actor
            ? Respond(await handlers.SendRecoveryAsync(new SendRecoveryCommand(actor, userId), cancellationToken), StatusCodes.Status202Accepted)
            : UnauthorizedFailure();

    public static int StatusFor(string code) => code switch
    {
        AdminUserErrorCodes.ValidationFailed or AdminUserErrorCodes.InvalidRole => StatusCodes.Status400BadRequest,
        AdminUserErrorCodes.UserNotFound => StatusCodes.Status404NotFound,
        _ => StatusCodes.Status409Conflict,
    };

    private IActionResult Respond<T>(AdminResult<T> result, int successStatus = StatusCodes.Status200OK)
        => result.Succeeded
            ? StatusCode(successStatus, ApiResponse<T>.Success(result.Value!, HttpContext.GetTraceId()))
            : StatusCode(StatusFor(result.ErrorCode!), Failure(result.ErrorCode!, result.Message!, result.Details));

    private Guid? ActorId()
        => Guid.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private IActionResult UnauthorizedFailure() => Unauthorized(Failure(AuthErrorCodes.Unauthorized, "Authentication is required."));

    private IActionResult ValidationFailure(FluentValidation.Results.ValidationResult validation)
        => BadRequest(Failure(AdminUserErrorCodes.ValidationFailed, "One or more fields are invalid.",
            validation.Errors.GroupBy(e => e.PropertyName).ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray())));

    private ApiResponse<object> Failure(string code, string message, object? details = null)
        => ApiResponse<object>.Failure(new ApiError(code, message, details), HttpContext.GetTraceId());
}
