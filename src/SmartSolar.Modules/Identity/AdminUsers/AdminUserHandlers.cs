using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Email;
using SmartSolar.Modules.Common.Messaging;
using SmartSolar.Modules.Common.Paging;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.Contracts.Persistence;
using SmartSolar.Modules.Identity.EmailVerification;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Identity.Events;
using SmartSolar.Modules.Identity.Options;
using SmartSolar.Modules.Identity.Security;

namespace SmartSolar.Modules.Identity.AdminUsers;

public static class AdminAuditActions
{
    public const string Created = "ACCOUNT_CREATED";
    public const string ProfileUpdated = "PROFILE_UPDATED";
    public const string EmailChanged = "EMAIL_CHANGED";
    public const string StatusChanged = "STATUS_CHANGED";
    public const string RolesChanged = "ROLES_CHANGED";
    public const string Deleted = "ACCOUNT_SOFT_DELETED";
    public const string SetupLinkIssued = "ACCOUNT_SETUP_LINK_ISSUED";
    public const string VerificationIssued = "EMAIL_VERIFICATION_ISSUED";
    public const string PasswordResetIssued = "PASSWORD_RESET_ISSUED";
}

/// <summary>
/// Administrative account management (ADMIN only). Security rules:
/// <list type="bullet">
/// <item>No password is ever chosen, stored in plain text or returned: provisioned accounts get a
/// single-use setup link by email, and the email is only verified when that link is used.</item>
/// <item>Every status, role, email or delete change increments <see cref="UserAccount.AuthVersion"/>,
/// which immediately invalidates access tokens already issued; deactivation also revokes refresh tokens.</item>
/// <item>The last active administrator cannot be removed, and administrators cannot deactivate or delete themselves.</item>
/// <item>Accounts that still hold upcoming technician appointments or open sales work cannot be deactivated
/// or lose that role until the work is reassigned.</item>
/// <item>Delete is a soft delete: history and foreign keys are preserved.</item>
/// </list>
/// </summary>
public sealed class AdminUserHandlers(
    IAdminUserStore store,
    SecureTokenFactory tokenFactory,
    IIntegrationEventPublisher publisher,
    IEmailDeliveryStatus emailDelivery,
    AccountSetupOptions setupOptions,
    EmailVerificationOptions verificationOptions,
    PasswordResetOptions resetOptions,
    FrontendOptions frontend,
    TimeProvider time,
    ILogger<AdminUserHandlers> logger)
{
    public const int MaxPageSize = 100;

    // ---------------------------------------------------------------- reads

    public async Task<AdminResult<PagedResult<AdminUserListItem>>> ListAsync(ListAdminUsersQuery query, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (query.Page < 1) errors["page"] = ["'page' must be 1 or greater."];
        if (query.PageSize is < 1 or > MaxPageSize) errors["pageSize"] = [$"'pageSize' must be between 1 and {MaxPageSize}."];

        string? role = null;
        if (!string.IsNullOrWhiteSpace(query.Role))
        {
            role = query.Role.Trim().ToUpperInvariant();
            if (!RoleCodes.All.Contains(role)) errors["role"] = ["Unknown role."];
        }

        UserStatus? status = null;
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (UserStatusCodes.TryParse(query.Status, out var parsed)) status = parsed;
            else errors["status"] = ["Unknown status."];
        }

        var sort = (query.Sort?.Trim().ToLowerInvariant()) switch
        {
            null or "" or "createdat" or "created" => AdminUserSort.CreatedAt,
            "name" or "fullname" => AdminUserSort.Name,
            "status" => AdminUserSort.Status,
            _ => (AdminUserSort?)null,
        };
        if (sort is null) errors["sort"] = ["'sort' must be one of name, createdAt, status."];

        var direction = query.Direction?.Trim().ToLowerInvariant();
        if (direction is not (null or "" or "asc" or "desc")) errors["direction"] = ["'direction' must be asc or desc."];

        var search = query.Search?.Trim();
        if (search is { Length: > 100 }) errors["search"] = ["'search' must be at most 100 characters."];

        if (errors.Count > 0)
            return AdminResult<PagedResult<AdminUserListItem>>.Fail(AdminUserErrorCodes.ValidationFailed, "One or more fields are invalid.", errors);

        // Newest first by default; names and statuses ascending by default.
        var descending = direction switch { "asc" => false, "desc" => true, _ => sort == AdminUserSort.CreatedAt };
        var page = await store.ListAsync(new AdminUserQuery(query.Page, query.PageSize, string.IsNullOrEmpty(search) ? null : search,
            role, status, query.IncludeDeleted, sort!.Value, descending), cancellationToken);

        return AdminResult<PagedResult<AdminUserListItem>>.Ok(new PagedResult<AdminUserListItem>(
            page.Items.Select(ListItem).ToList(), page.Page, page.PageSize, page.TotalItems, page.TotalPages));
    }

    public async Task<AdminResult<AdminUserDetail>> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var row = await store.GetAsync(userId, cancellationToken);
        return row is null
            ? AdminResult<AdminUserDetail>.Fail(AdminUserErrorCodes.UserNotFound, "User was not found.")
            : AdminResult<AdminUserDetail>.Ok(await DetailAsync(row, cancellationToken));
    }

    // ---------------------------------------------------------------- create

    public async Task<AdminResult<CreatedAdminUser>> CreateAsync(CreateAdminUserCommand command, CancellationToken cancellationToken)
    {
        var email = EmailNormalizer.Normalize(command.Email);
        var roles = await ResolveRolesAsync(command.Roles, cancellationToken);
        if (roles.Error is not null) return AdminResult<CreatedAdminUser>.Fail(AdminUserErrorCodes.InvalidRole, roles.Error);

        if (await store.EmailTakenAsync(email, null, cancellationToken))
            return AdminResult<CreatedAdminUser>.Fail(AdminUserErrorCodes.EmailAlreadyExists, "An account with this email already exists.");

        PendingLink link;
        Guid userId;
        try
        {
            (userId, link) = await store.ExecuteInTransactionAsync<(Guid, PendingLink)>(token =>
            {
                var now = time.GetUtcNow();
                var user = new UserAccount
                {
                    Id = Guid.NewGuid(),
                    Email = email,
                    FullName = command.FullName.Trim(),
                    Phone = Clean(command.Phone),
                    // No password exists yet and the email is unproven: the setup link proves both.
                    PasswordHash = null,
                    Status = UserStatus.PendingVerification,
                    EmailVerifiedAt = null,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                store.AddUser(user);
                foreach (var roleId in roles.Ids!.Values)
                {
                    store.AddUserRole(new UserRole { UserId = user.Id, RoleId = roleId, AssignedAt = now, AssignedBy = command.ActorUserId });
                }

                var pending = IssueToken(user, AuthActionTokenType.AccountSetup, now);
                Audit(user.Id, command.ActorUserId, AdminAuditActions.Created, $"roles={string.Join(",", roles.Ids.Keys.Order())}", now);
                Audit(user.Id, command.ActorUserId, AdminAuditActions.SetupLinkIssued, null, now);
                return Task.FromResult(((user.Id, pending), true));
            }, cancellationToken);
        }
        catch (DuplicateEmailException)
        {
            return AdminResult<CreatedAdminUser>.Fail(AdminUserErrorCodes.EmailAlreadyExists, "An account with this email already exists.");
        }

        var activation = await DeliverAsync(link, cancellationToken);
        logger.LogInformation("Admin {ActorId} provisioned account {UserId}.", command.ActorUserId, userId);
        var detail = await DetailAsync((await store.GetAsync(userId, cancellationToken))!, cancellationToken);
        return AdminResult<CreatedAdminUser>.Ok(new CreatedAdminUser(detail, activation));
    }

    // ---------------------------------------------------------------- update

    public async Task<AdminResult<UpdatedAdminUser>> UpdateAsync(UpdateAdminUserCommand command, CancellationToken cancellationToken)
    {
        var newEmail = string.IsNullOrWhiteSpace(command.Email) ? null : EmailNormalizer.Normalize(command.Email);
        if (newEmail is not null && await store.EmailTakenAsync(newEmail, command.UserId, cancellationToken))
            return AdminResult<UpdatedAdminUser>.Fail(AdminUserErrorCodes.EmailAlreadyExists, "An account with this email already exists.");

        Outcome<PendingLink?> outcome;
        try
        {
            outcome = await store.ExecuteInTransactionAsync<Outcome<PendingLink?>>(async token =>
            {
                var user = await store.FindForUpdateAsync(command.UserId, token);
                if (user is null) return (Outcome<PendingLink?>.Fail(AdminUserErrorCodes.UserNotFound, "User was not found."), false);
                if (user.DeletedAt is not null) return (Outcome<PendingLink?>.Fail(AdminUserErrorCodes.AccountDeleted, "Deleted accounts cannot be changed."), false);

                var now = time.GetUtcNow();
                user.FullName = command.FullName.Trim();
                user.Phone = Clean(command.Phone);
                user.UpdatedAt = now;
                Audit(user.Id, command.ActorUserId, AdminAuditActions.ProfileUpdated, null, now);

                PendingLink? link = null;
                if (newEmail is not null && newEmail != user.Email)
                {
                    // A new address is unproven: it is not verified, existing sessions end, and a
                    // verification (or account-setup) link goes to the new address.
                    user.Email = newEmail;
                    user.EmailVerifiedAt = null;
                    user.AuthVersion++;
                    await store.RevokeActiveRefreshTokensAsync(user.Id, now, token);
                    link = user.PasswordHash is null
                        ? IssueToken(user, AuthActionTokenType.AccountSetup, now)
                        : IssueToken(user, AuthActionTokenType.EmailVerification, now);
                    Audit(user.Id, command.ActorUserId, AdminAuditActions.EmailChanged, "verification required", now);
                }
                return (Outcome<PendingLink?>.Ok(link), true);
            }, cancellationToken);
        }
        catch (DuplicateEmailException)
        {
            return AdminResult<UpdatedAdminUser>.Fail(AdminUserErrorCodes.EmailAlreadyExists, "An account with this email already exists.");
        }

        if (!outcome.Succeeded) return AdminResult<UpdatedAdminUser>.Fail(outcome.Code!, outcome.Message!);
        var activation = outcome.Value is { } pending ? await DeliverAsync(pending, cancellationToken) : null;
        var detail = await DetailAsync((await store.GetAsync(command.UserId, cancellationToken))!, cancellationToken);
        return AdminResult<UpdatedAdminUser>.Ok(new UpdatedAdminUser(detail, activation));
    }

    // ---------------------------------------------------------------- status

    public async Task<AdminResult<AdminUserDetail>> ChangeStatusAsync(ChangeUserStatusCommand command, CancellationToken cancellationToken)
    {
        UserStatusCodes.TryParse(command.Status, out var target);
        if (command.ActorUserId == command.UserId)
            return AdminResult<AdminUserDetail>.Fail(AdminUserErrorCodes.CannotModifyOwnAccount, "Administrators cannot change the status of their own account.");

        var outcome = await store.ExecuteInTransactionAsync<Outcome<bool>>(async token =>
        {
            await store.LockAdministratorsAsync(token);
            await store.LockTechnicianSchedulingAsync(command.UserId, token);
            var user = await store.FindForUpdateAsync(command.UserId, token);
            if (user is null) return (Outcome<bool>.Fail(AdminUserErrorCodes.UserNotFound, "User was not found."), false);
            if (user.DeletedAt is not null) return (Outcome<bool>.Fail(AdminUserErrorCodes.AccountDeleted, "Deleted accounts cannot be changed."), false);
            if (user.Status == target) return (Outcome<bool>.Ok(false), false);  // idempotent

            var transition = CheckTransition(user, target);
            if (transition is not null) return (Outcome<bool>.Fail(AdminUserErrorCodes.InvalidStatusTransition, transition), false);

            if (user.Status == UserStatus.Active && target != UserStatus.Active)
            {
                var blocked = await CheckDeactivationAsync(user.Id, await store.GetRoleCodesAsync(user.Id, token), token);
                if (blocked is not null) return (blocked.As<bool>(), false);
            }

            var now = time.GetUtcNow();
            Audit(user.Id, command.ActorUserId, AdminAuditActions.StatusChanged,
                Truncate($"{UserStatusCodes.ToCode(user.Status)} -> {UserStatusCodes.ToCode(target)}{(string.IsNullOrWhiteSpace(command.Reason) ? "" : ": " + command.Reason.Trim())}", 500), now);
            user.Status = target;
            user.UpdatedAt = now;
            user.AuthVersion++;
            if (target != UserStatus.Active)
            {
                await store.RevokeActiveRefreshTokensAsync(user.Id, now, token);
            }
            return (Outcome<bool>.Ok(true), true);
        }, cancellationToken);

        if (!outcome.Succeeded) return AdminResult<AdminUserDetail>.Fail(outcome.Code!, outcome.Message!, outcome.Details);
        logger.LogInformation("Admin {ActorId} set account {UserId} to {Status}.", command.ActorUserId, command.UserId, UserStatusCodes.ToCode(target));
        return await GetAsync(command.UserId, cancellationToken);
    }

    /// <summary>Allowed transitions. Nothing moves back to PENDING_VERIFICATION, and nothing becomes ACTIVE without a verified email.</summary>
    private static string? CheckTransition(UserAccount user, UserStatus target) => target switch
    {
        UserStatus.PendingVerification => "Accounts cannot be moved back to PENDING_VERIFICATION manually.",
        UserStatus.Active when user.EmailVerifiedAt is null =>
            "The email address has not been verified. Send the account-setup or verification link instead of activating manually.",
        UserStatus.Suspended when user.Status != UserStatus.Active => "Only ACTIVE accounts can be suspended.",
        _ => null,
    };

    // ---------------------------------------------------------------- roles

    public async Task<AdminResult<AdminUserDetail>> ReplaceRolesAsync(ReplaceUserRolesCommand command, CancellationToken cancellationToken)
    {
        var roles = await ResolveRolesAsync(command.Roles, cancellationToken);
        if (roles.Error is not null) return AdminResult<AdminUserDetail>.Fail(AdminUserErrorCodes.InvalidRole, roles.Error);

        var outcome = await store.ExecuteInTransactionAsync<Outcome<bool>>(async token =>
        {
            await store.LockAdministratorsAsync(token);
            await store.LockTechnicianSchedulingAsync(command.UserId, token);
            var user = await store.FindForUpdateAsync(command.UserId, token);
            if (user is null) return (Outcome<bool>.Fail(AdminUserErrorCodes.UserNotFound, "User was not found."), false);
            if (user.DeletedAt is not null) return (Outcome<bool>.Fail(AdminUserErrorCodes.AccountDeleted, "Deleted accounts cannot be changed."), false);

            var current = (await store.GetRoleCodesAsync(user.Id, token)).ToHashSet(StringComparer.Ordinal);
            var requested = roles.Ids!.Keys.ToHashSet(StringComparer.Ordinal);
            if (current.SetEquals(requested)) return (Outcome<bool>.Ok(false), false);  // idempotent

            var removed = current.Except(requested).ToList();
            if (user.Status == UserStatus.Active && removed.Count > 0)
            {
                var blocked = await CheckDeactivationAsync(user.Id, removed, token);
                if (blocked is not null) return (blocked.As<bool>(), false);
            }

            var now = time.GetUtcNow();
            await store.ReplaceRolesAsync(user.Id, roles.Ids.Values.ToList(), command.ActorUserId, now, token);
            user.AuthVersion++;   // tokens carrying the old roles stop working immediately
            user.UpdatedAt = now;
            Audit(user.Id, command.ActorUserId, AdminAuditActions.RolesChanged,
                $"{string.Join(",", current.Order())} -> {string.Join(",", requested.Order())}", now);
            return (Outcome<bool>.Ok(true), true);
        }, cancellationToken);

        if (!outcome.Succeeded) return AdminResult<AdminUserDetail>.Fail(outcome.Code!, outcome.Message!, outcome.Details);
        return await GetAsync(command.UserId, cancellationToken);
    }

    // ---------------------------------------------------------------- delete

    /// <summary>Soft delete. Returns false when the account was already deleted (idempotent).</summary>
    public async Task<AdminResult<bool>> DeleteAsync(DeleteAdminUserCommand command, CancellationToken cancellationToken)
    {
        if (command.ActorUserId == command.UserId)
            return AdminResult<bool>.Fail(AdminUserErrorCodes.CannotModifyOwnAccount, "Administrators cannot delete their own account.");

        var outcome = await store.ExecuteInTransactionAsync<Outcome<bool>>(async token =>
        {
            await store.LockAdministratorsAsync(token);
            await store.LockTechnicianSchedulingAsync(command.UserId, token);
            var user = await store.FindForUpdateAsync(command.UserId, token);
            if (user is null) return (Outcome<bool>.Fail(AdminUserErrorCodes.UserNotFound, "User was not found."), false);
            if (user.DeletedAt is not null) return (Outcome<bool>.Ok(false), false);

            var blocked = await CheckDeactivationAsync(user.Id, await store.GetRoleCodesAsync(user.Id, token), token,
                checkLastAdmin: user.Status == UserStatus.Active);
            if (blocked is not null) return (blocked.As<bool>(), false);

            var now = time.GetUtcNow();
            // Rows stay for history and foreign keys; the account can never sign in or get work again.
            user.DeletedAt = now;
            user.Status = UserStatus.Disabled;
            user.UpdatedAt = now;
            user.AuthVersion++;
            await store.RevokeActiveRefreshTokensAsync(user.Id, now, token);
            await store.RemoveUnusedActionTokensAsync(user.Id, null, token);
            Audit(user.Id, command.ActorUserId, AdminAuditActions.Deleted, Truncate(command.Reason?.Trim(), 500), now);
            return (Outcome<bool>.Ok(true), true);
        }, cancellationToken);

        if (!outcome.Succeeded) return AdminResult<bool>.Fail(outcome.Code!, outcome.Message!, outcome.Details);
        logger.LogInformation("Admin {ActorId} soft-deleted account {UserId}.", command.ActorUserId, command.UserId);
        return AdminResult<bool>.Ok(outcome.Value);
    }

    // ---------------------------------------------------------------- recovery

    /// <summary>
    /// Emails the right one-time link for the account's state: account setup (never set a
    /// password), email verification (password set, email unproven) or password reset (active).
    /// The admin never sees the password or the link.
    /// </summary>
    public async Task<AdminResult<RecoveryIssued>> SendRecoveryAsync(SendRecoveryCommand command, CancellationToken cancellationToken)
    {
        var outcome = await store.ExecuteInTransactionAsync<Outcome<PendingLink>>(async token =>
        {
            var user = await store.FindForUpdateAsync(command.UserId, token);
            if (user is null) return (Outcome<PendingLink>.Fail(AdminUserErrorCodes.UserNotFound, "User was not found."), false);
            if (user.DeletedAt is not null) return (Outcome<PendingLink>.Fail(AdminUserErrorCodes.AccountDeleted, "Deleted accounts cannot be recovered."), false);

            var now = time.GetUtcNow();
            AuthActionTokenType? type = user switch
            {
                { Status: UserStatus.PendingVerification, PasswordHash: null } => AuthActionTokenType.AccountSetup,
                { Status: UserStatus.PendingVerification } => AuthActionTokenType.EmailVerification,
                { Status: UserStatus.Active, EmailVerifiedAt: null, PasswordHash: null } => AuthActionTokenType.AccountSetup,
                { Status: UserStatus.Active } => AuthActionTokenType.PasswordReset,
                _ => null,
            };
            if (type is null)
                return (Outcome<PendingLink>.Fail(AdminUserErrorCodes.RecoveryNotAllowed, "Suspended or disabled accounts must be reactivated before recovery."), false);

            await store.RemoveUnusedActionTokensAsync(user.Id, type, token);
            var link = IssueToken(user, type.Value, now);
            Audit(user.Id, command.ActorUserId, type switch
            {
                AuthActionTokenType.AccountSetup => AdminAuditActions.SetupLinkIssued,
                AuthActionTokenType.EmailVerification => AdminAuditActions.VerificationIssued,
                _ => AdminAuditActions.PasswordResetIssued,
            }, null, now);
            return (Outcome<PendingLink>.Ok(link), true);
        }, cancellationToken);

        if (!outcome.Succeeded) return AdminResult<RecoveryIssued>.Fail(outcome.Code!, outcome.Message!);
        return AdminResult<RecoveryIssued>.Ok(new RecoveryIssued(command.UserId, await DeliverAsync(outcome.Value!, cancellationToken)));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Blocks removing a capability that still has live work, and removing the last active admin.</summary>
    private async Task<Outcome<object>?> CheckDeactivationAsync(Guid userId, IReadOnlyCollection<string> capabilitiesRemoved, CancellationToken token, bool checkLastAdmin = true)
    {
        if (checkLastAdmin && capabilitiesRemoved.Contains(RoleCodes.Admin) && await store.CountOtherActiveAdminsAsync(userId, token) == 0)
            return Outcome<object>.Fail(AdminUserErrorCodes.LastActiveAdmin, "This is the last active administrator; promote another administrator first.");

        var checkTechnician = capabilitiesRemoved.Contains(RoleCodes.Technician);
        var checkSales = capabilitiesRemoved.Contains(RoleCodes.Sales);
        if (!checkTechnician && !checkSales) return null;

        var work = await store.GetOperationalWorkAsync(userId, time.GetUtcNow(), token);
        var appointments = checkTechnician ? work.TechnicianAppointments : [];
        var requests = checkSales ? work.OwnedOpenSurveyRequestIds : [];
        if (appointments.Count == 0 && requests.Count == 0) return null;

        return Outcome<object>.Fail(AdminUserErrorCodes.AccountHasActiveWork,
            "The account still has upcoming technician appointments or owned open survey requests. Reassign confirmed appointments "
            + "(POST /api/admin/survey-appointments/{id}/assign-technician), disable the technician's scheduling first so pending holds move "
            + "automatically, and move owned requests (PUT /api/admin/survey-requests/{id}/sales-owner).",
            new { technicianAppointments = appointments, ownedOpenSurveyRequestIds = requests });
    }

    private async Task<(IReadOnlyDictionary<string, Guid>? Ids, string? Error)> ResolveRolesAsync(IReadOnlyList<string>? requested, CancellationToken cancellationToken)
    {
        var codes = (requested ?? []).Select(r => r?.Trim().ToUpperInvariant() ?? "").Distinct().ToList();
        if (codes.Count == 0) return (null, "At least one role is required.");
        var unknown = codes.Where(c => !RoleCodes.All.Contains(c)).ToList();
        if (unknown.Count > 0) return (null, $"Unknown role(s): {string.Join(", ", unknown)}.");
        var ids = await store.GetRoleIdsAsync(codes, cancellationToken);
        if (ids.Count != codes.Count) return (null, "One or more roles are not configured in this environment.");
        return (ids, null);
    }

    private PendingLink IssueToken(UserAccount user, AuthActionTokenType type, DateTimeOffset now)
    {
        var token = tokenFactory.Create();
        var lifetime = type switch
        {
            AuthActionTokenType.AccountSetup => setupOptions.LifetimeMinutes,
            AuthActionTokenType.EmailVerification => verificationOptions.LifetimeMinutes,
            _ => resetOptions.LifetimeMinutes,
        };
        var expiresAt = now.AddMinutes(lifetime);
        store.AddActionToken(new AuthActionToken { Id = Guid.NewGuid(), UserId = user.Id, TokenHash = token.TokenHash, Type = type, ExpiresAt = expiresAt });
        return new PendingLink(user.Id, user.Email, user.FullName, type, token.RawToken, expiresAt);
    }

    /// <summary>Publishes the link after commit. The raw token is only placed in the event, never logged or returned.</summary>
    private async Task<ActivationState> DeliverAsync(PendingLink link, CancellationToken cancellationToken)
    {
        var status = link.Type switch
        {
            AuthActionTokenType.AccountSetup => ActivationStatuses.PendingAccountSetup,
            AuthActionTokenType.EmailVerification => ActivationStatuses.PendingEmailVerification,
            _ => ActivationStatuses.PasswordResetIssued,
        };

        if (!emailDelivery.CanDeliver)
        {
            return new ActivationState(status, EmailDeliveryStates.Disabled, link.ExpiresAt,
                "Email delivery is disabled in this environment, so the link was NOT sent. Enable RabbitMq and Email, then use send-password-reset to issue a new link.");
        }

        try
        {
            object message = link.Type switch
            {
                AuthActionTokenType.AccountSetup => new AccountSetupRequestedEvent(link.UserId, link.Email, link.FullName,
                    VerificationUrlBuilder.Build(frontend.EffectiveAccountSetupUrl, link.RawToken)),
                AuthActionTokenType.EmailVerification => new EmailVerificationRequestedEvent(link.UserId, link.Email, link.FullName,
                    VerificationUrlBuilder.Build(frontend.EmailVerificationUrl, link.RawToken)),
                _ => new PasswordResetRequestedEvent(link.UserId, link.Email, link.FullName,
                    VerificationUrlBuilder.Build(frontend.PasswordResetUrl, link.RawToken)),
            };
            switch (message)
            {
                case AccountSetupRequestedEvent e: await publisher.PublishAsync(e, cancellationToken); break;
                case EmailVerificationRequestedEvent e: await publisher.PublishAsync(e, cancellationToken); break;
                case PasswordResetRequestedEvent e: await publisher.PublishAsync(e, cancellationToken); break;
            }
            return new ActivationState(status, EmailDeliveryStates.Queued, link.ExpiresAt, "A one-time link was queued for delivery to the account's email address.");
        }
        catch (Exception ex)
        {
            // The URL carries the raw token, so only the user id is logged.
            logger.LogError(ex, "Failed to publish account link for user {UserId}.", link.UserId);
            return new ActivationState(status, EmailDeliveryStates.PublishFailed, link.ExpiresAt,
                "The link could not be queued. Use send-password-reset to try again.");
        }
    }

    private void Audit(Guid target, Guid actor, string action, string? detail, DateTimeOffset now)
        => store.AddAudit(new UserAccountAuditEntry { Id = Guid.NewGuid(), TargetUserId = target, ActorUserId = actor, Action = action, Detail = detail, CreatedAt = now });

    private async Task<AdminUserDetail> DetailAsync(AdminUserRow row, CancellationToken cancellationToken)
        => new(row.Id, row.Email, row.FullName, row.Phone, UserStatusCodes.ToCode(row.Status), row.Roles, row.EmailVerifiedAt, row.LastLoginAt,
            row.CreatedAt, row.UpdatedAt, row.DeletedAt, row.HasPassword,
            row.Roles.Contains(RoleCodes.Technician) ? await store.GetTechnicianSummaryAsync(row.Id, cancellationToken) : null);

    private static AdminUserListItem ListItem(AdminUserRow row)
        => new(row.Id, row.Email, row.FullName, row.Phone, UserStatusCodes.ToCode(row.Status), row.Roles, row.EmailVerifiedAt, row.LastLoginAt,
            row.CreatedAt, row.UpdatedAt, row.DeletedAt);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];

    private sealed record PendingLink(Guid UserId, string Email, string FullName, AuthActionTokenType Type, string RawToken, DateTimeOffset ExpiresAt);

    private sealed record Outcome<T>(T? Value, string? Code, string? Message, object? Details = null)
    {
        public bool Succeeded => Code is null;
        public static Outcome<T> Ok(T value) => new(value, null, null);
        public static Outcome<T> Fail(string code, string message, object? details = null) => new(default, code, message, details);
        public Outcome<TOther> As<TOther>() => new(default, Code, Message, Details);
    }
}
