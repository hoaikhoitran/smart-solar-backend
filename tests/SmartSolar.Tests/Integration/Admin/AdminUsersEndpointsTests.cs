using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.AdminUsers;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Identity.Events;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.Admin;

/// <summary>Admin account management through the real pipeline (JWT + per-request auth-version check).</summary>
public sealed class AdminUsersEndpointsTests
{
    private const string Password = "correct horse battery";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<JsonElement> DataOf(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await BodyOf(r);
        Assert.True(expected == r.StatusCode, $"Expected {expected} but got {(int)r.StatusCode}: {body}");
        Assert.True(body.GetProperty("isSuccess").GetBoolean());
        return body.GetProperty("data");
    }

    private static async Task AssertFailureAsync(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        var body = await BodyOf(r);
        Assert.True(status == r.StatusCode, $"Expected {status} but got {(int)r.StatusCode}: {body}");
        Assert.False(body.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
    }

    private static HttpClient Admin(AuthApiFactory factory, Guid? id = null) => CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin, id);

    private static HttpClient WithToken(AuthApiFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<Guid> CreateAsync(HttpClient admin, string email, string name, params string[] roles)
    {
        var data = await DataOf(await admin.PostAsJsonAsync("/api/admin/users", new { email, fullName = name, phone = "0900000000", roles }), HttpStatusCode.Created);
        return data.GetProperty("user").GetProperty("id").GetGuid();
    }

    /// <summary>Creates an account through the admin API and completes setup with the emailed link.</summary>
    private static async Task<(Guid UserId, string AccessToken, string RefreshToken)> ProvisionAndSignInAsync(
        AuthApiFactory factory, HttpClient admin, string email, params string[] roles)
    {
        var userId = await CreateAsync(admin, email, "Provisioned", roles);
        var link = factory.Publisher.PublishedOf<AccountSetupRequestedEvent>().Last(e => e.UserId == userId).SetupUrl;
        var token = Uri.UnescapeDataString(link[(link.IndexOf("token=", StringComparison.Ordinal) + 6)..]);

        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = Password })).StatusCode);
        var login = await DataOf(await anonymous.PostAsJsonAsync("/api/auth/login", new { email, password = Password }));
        return (userId, login.GetProperty("accessToken").GetString()!, login.GetProperty("refreshToken").GetString()!);
    }

    [Theory]
    [InlineData(RoleCodes.Customer)]
    [InlineData(RoleCodes.Sales)]
    [InlineData(RoleCodes.Technician)]
    [InlineData(RoleCodes.Manager)]
    public async Task Non_admin_roles_cannot_use_the_admin_user_api(string role)
    {
        using var factory = new AuthApiFactory();
        var client = CatalogEndpointsTests.ClientFor(factory, role);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/users",
            new { email = "x@example.com", fullName = "X", roles = new[] { "ADMIN" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/admin/users")).StatusCode);
    }

    [Fact]
    public async Task Creating_a_technician_issues_a_setup_link_and_never_returns_credentials()
    {
        using var factory = new AuthApiFactory { EmailCanDeliver = true };
        var admin = Admin(factory);

        var response = await admin.PostAsJsonAsync("/api/admin/users", new
        {
            email = "  Technician@Example.com ", fullName = "Nguyen Van A", phone = "0900000000", roles = new[] { "TECHNICIAN" }
        });
        var raw = await response.Content.ReadAsStringAsync();
        var data = await DataOf(response, HttpStatusCode.Created);

        var user = data.GetProperty("user");
        Assert.Equal("technician@example.com", user.GetProperty("email").GetString());
        Assert.Equal("PENDING_VERIFICATION", user.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, user.GetProperty("emailVerifiedAt").ValueKind);
        Assert.Equal(["TECHNICIAN"], user.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToArray());
        Assert.False(user.GetProperty("hasPassword").GetBoolean());
        Assert.False(user.GetProperty("technician").GetProperty("hasProfile").GetBoolean());
        Assert.Equal(ActivationStatuses.PendingAccountSetup, data.GetProperty("activation").GetProperty("status").GetString());
        Assert.Equal(EmailDeliveryStates.Queued, data.GetProperty("activation").GetProperty("emailDelivery").GetString());

        var sent = Assert.Single(factory.Publisher.PublishedOf<AccountSetupRequestedEvent>());
        var rawToken = Uri.UnescapeDataString(sent.SetupUrl[(sent.SetupUrl.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
        Assert.DoesNotContain(rawToken, raw);
        Assert.DoesNotContain("passwordHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tokenHash", raw, StringComparison.OrdinalIgnoreCase);

        await using var db = factory.CreateDbContext();
        var stored = await db.UserAccounts.SingleAsync(u => u.Email == "technician@example.com");
        Assert.Null(stored.PasswordHash);
        var setup = await db.AuthActionTokens.SingleAsync(t => t.UserId == stored.Id);
        Assert.Equal(AuthActionTokenType.AccountSetup, setup.Type);
        Assert.NotEqual(rawToken, setup.TokenHash);
        var assignedBy = (await db.UserRoles.SingleAsync(r => r.UserId == stored.Id)).AssignedBy;
        Assert.NotNull(assignedBy);
        Assert.Contains(await db.UserAccountAuditEntries.Where(a => a.TargetUserId == stored.Id).Select(a => a.Action).ToListAsync(), a => a == AdminAuditActions.Created);
    }

    [Fact]
    public async Task Provisioned_account_activates_only_through_the_setup_link_and_can_then_sign_in()
    {
        using var factory = new AuthApiFactory { EmailCanDeliver = true };
        var admin = Admin(factory);
        var userId = await CreateAsync(admin, "sales@example.com", "Sales Person", "SALES");

        // Unverified: login is refused and an admin cannot force ACTIVE.
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "sales@example.com", password = Password })).StatusCode);
        await AssertFailureAsync(await admin.PatchAsJsonAsync($"/api/admin/users/{userId}/status", new { status = "ACTIVE" }),
            HttpStatusCode.Conflict, AdminUserErrorCodes.InvalidStatusTransition);

        var (_, access, _) = await ProvisionAndSignInAsync(factory, admin, "tech2@example.com", "TECHNICIAN");
        Assert.False(string.IsNullOrWhiteSpace(access));

        await using var db = factory.CreateDbContext();
        var tech = await db.UserAccounts.SingleAsync(u => u.Email == "tech2@example.com");
        Assert.Equal(UserStatus.Active, tech.Status);
        Assert.NotNull(tech.EmailVerifiedAt);
        Assert.NotNull(tech.PasswordHash);
    }

    [Fact]
    public async Task Setup_link_is_single_use()
    {
        using var factory = new AuthApiFactory { EmailCanDeliver = true };
        var admin = Admin(factory);
        var userId = await CreateAsync(admin, "once@example.com", "Once", "SALES");
        var link = factory.Publisher.PublishedOf<AccountSetupRequestedEvent>().Single().SetupUrl;
        var token = Uri.UnescapeDataString(link[(link.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = "another long password" })).StatusCode);
    }

    [Fact]
    public async Task Email_delivery_disabled_is_reported_instead_of_pretending()
    {
        using var factory = new AuthApiFactory();
        var data = await DataOf(await Admin(factory).PostAsJsonAsync("/api/admin/users",
            new { email = "nomail@example.com", fullName = "No Mail", roles = new[] { "SALES" } }), HttpStatusCode.Created);

        Assert.Equal(EmailDeliveryStates.Disabled, data.GetProperty("activation").GetProperty("emailDelivery").GetString());
        Assert.Equal("PENDING_VERIFICATION", data.GetProperty("user").GetProperty("status").GetString());
        Assert.Empty(factory.Publisher.PublishedOf<AccountSetupRequestedEvent>());
    }

    [Fact]
    public async Task Duplicate_email_and_unknown_roles_are_rejected()
    {
        using var factory = new AuthApiFactory();
        var admin = Admin(factory);
        await CreateAsync(admin, "dup@example.com", "First", "SALES");

        await AssertFailureAsync(await admin.PostAsJsonAsync("/api/admin/users", new { email = "DUP@example.com", fullName = "Second", roles = new[] { "SALES" } }),
            HttpStatusCode.Conflict, AuthErrorCodes.EmailAlreadyExists);
        await AssertFailureAsync(await admin.PostAsJsonAsync("/api/admin/users", new { email = "r@example.com", fullName = "R", roles = new[] { "SUPERUSER" } }),
            HttpStatusCode.BadRequest, AdminUserErrorCodes.InvalidRole);
        await AssertFailureAsync(await admin.PostAsJsonAsync("/api/admin/users", new { email = "r@example.com", fullName = "R", roles = Array.Empty<string>() }),
            HttpStatusCode.BadRequest, AdminUserErrorCodes.ValidationFailed);
        await AssertFailureAsync(await admin.PostAsJsonAsync("/api/admin/users", new { email = "not-an-email", fullName = "R", roles = new[] { "SALES" } }),
            HttpStatusCode.BadRequest, AdminUserErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task List_supports_search_role_status_sort_and_pagination()
    {
        using var factory = new AuthApiFactory();
        var admin = Admin(factory);
        await CreateAsync(admin, "anna@example.com", "Anna Sales", "SALES");
        await CreateAsync(admin, "bao@example.com", "Bao Technician", "TECHNICIAN");
        await CreateAsync(admin, "chi@example.com", "Chi Technician", "TECHNICIAN");

        var technicians = await DataOf(await admin.GetAsync("/api/admin/users?role=TECHNICIAN&sort=name&direction=desc"));
        Assert.Equal(2, technicians.GetProperty("totalItems").GetInt32());
        Assert.Equal(["Chi Technician", "Bao Technician"], technicians.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("fullName").GetString()).ToArray());

        var search = await DataOf(await admin.GetAsync("/api/admin/users?search=ANNA"));
        Assert.Equal("anna@example.com", Assert.Single(search.GetProperty("items").EnumerateArray()).GetProperty("email").GetString());

        var paged = await DataOf(await admin.GetAsync("/api/admin/users?status=PENDING_VERIFICATION&pageSize=2&page=2&sort=name"));
        Assert.Equal(3, paged.GetProperty("totalItems").GetInt32());
        Assert.Equal(2, paged.GetProperty("totalPages").GetInt32());
        Assert.Equal("Chi Technician", Assert.Single(paged.GetProperty("items").EnumerateArray()).GetProperty("fullName").GetString());

        await AssertFailureAsync(await admin.GetAsync("/api/admin/users?pageSize=500"), HttpStatusCode.BadRequest, AdminUserErrorCodes.ValidationFailed);
        await AssertFailureAsync(await admin.GetAsync("/api/admin/users?sort=password"), HttpStatusCode.BadRequest, AdminUserErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Detail_and_profile_update_work_and_email_change_requires_new_verification()
    {
        using var factory = new AuthApiFactory { EmailCanDeliver = true };
        var admin = Admin(factory);
        var (userId, access, _) = await ProvisionAndSignInAsync(factory, admin, "staff@example.com", "SALES");

        var detail = await DataOf(await admin.GetAsync($"/api/admin/users/{userId}"));
        Assert.Equal("ACTIVE", detail.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, detail.GetProperty("emailVerifiedAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, detail.GetProperty("lastLoginAt").ValueKind);

        var renamed = await DataOf(await admin.PutAsJsonAsync($"/api/admin/users/{userId}", new { fullName = "Renamed", phone = "0911111111" }));
        Assert.Equal("Renamed", renamed.GetProperty("user").GetProperty("fullName").GetString());
        Assert.Equal(JsonValueKind.Null, renamed.GetProperty("activation").ValueKind);

        var moved = await DataOf(await admin.PutAsJsonAsync($"/api/admin/users/{userId}", new { fullName = "Renamed", email = "new-address@example.com" }));
        Assert.Equal("new-address@example.com", moved.GetProperty("user").GetProperty("email").GetString());
        Assert.Equal(JsonValueKind.Null, moved.GetProperty("user").GetProperty("emailVerifiedAt").ValueKind);
        Assert.Equal(ActivationStatuses.PendingEmailVerification, moved.GetProperty("activation").GetProperty("status").GetString());
        Assert.Single(factory.Publisher.PublishedOf<EmailVerificationRequestedEvent>(), e => e.Email == "new-address@example.com");

        // Existing sessions end with the email change.
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithToken(factory, access).GetAsync("/api/survey-requests/pending")).StatusCode);
        await AssertFailureAsync(await admin.GetAsync($"/api/admin/users/{Guid.NewGuid()}"), HttpStatusCode.NotFound, AdminUserErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task Suspending_a_user_rejects_the_existing_access_and_refresh_tokens_immediately()
    {
        using var factory = new AuthApiFactory { EmailCanDeliver = true };
        var admin = Admin(factory);
        var (userId, access, refresh) = await ProvisionAndSignInAsync(factory, admin, "seller@example.com", "SALES");
        var session = WithToken(factory, access);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/survey-requests/pending")).StatusCode);

        var suspended = await DataOf(await admin.PatchAsJsonAsync($"/api/admin/users/{userId}/status", new { status = "SUSPENDED", reason = "investigation" }));
        Assert.Equal("SUSPENDED", suspended.GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/survey-requests/pending")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "seller@example.com", password = Password })).StatusCode);

        // Reactivation works because the email was verified through the setup link; old tokens stay dead.
        Assert.Equal("ACTIVE", (await DataOf(await admin.PatchAsJsonAsync($"/api/admin/users/{userId}/status", new { status = "ACTIVE" }))).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/survey-requests/pending")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "seller@example.com", password = Password })).StatusCode);
    }

    [Fact]
    public async Task Role_revocation_invalidates_tokens_that_still_carry_the_old_role()
    {
        using var factory = new AuthApiFactory { EmailCanDeliver = true };
        var admin = Admin(factory);
        var (userId, access, refresh) = await ProvisionAndSignInAsync(factory, admin, "manager@example.com", "MANAGER", "SALES");
        var session = WithToken(factory, access);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/survey-requests/pending")).StatusCode);

        var updated = await DataOf(await admin.PutAsJsonAsync($"/api/admin/users/{userId}/roles", new { roles = new[] { "MANAGER" } }));
        Assert.Equal(["MANAGER"], updated.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToArray());

        // The stale token still claims SALES but is rejected outright.
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/survey-requests/pending")).StatusCode);

        // A refreshed session carries only the current roles.
        var refreshed = await DataOf(await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh }));
        var fresh = WithToken(factory, refreshed.GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.Forbidden, (await fresh.GetAsync("/api/survey-requests/pending")).StatusCode);
    }

    [Fact]
    public async Task The_last_active_admin_cannot_be_removed_and_admins_cannot_disable_themselves()
    {
        using var factory = new AuthApiFactory();
        var adminId = Guid.NewGuid();
        var admin = Admin(factory, adminId);

        await AssertFailureAsync(await admin.PutAsJsonAsync($"/api/admin/users/{adminId}/roles", new { roles = new[] { "SALES" } }),
            HttpStatusCode.Conflict, AdminUserErrorCodes.LastActiveAdmin);
        await AssertFailureAsync(await admin.PatchAsJsonAsync($"/api/admin/users/{adminId}/status", new { status = "DISABLED" }),
            HttpStatusCode.Conflict, AdminUserErrorCodes.CannotModifyOwnAccount);
        await AssertFailureAsync(await admin.DeleteAsync($"/api/admin/users/{adminId}"),
            HttpStatusCode.Conflict, AdminUserErrorCodes.CannotModifyOwnAccount);

        // With a second active admin, removing the first one's ADMIN role is allowed.
        var secondId = Guid.NewGuid();
        var second = Admin(factory, secondId);
        Assert.Equal(HttpStatusCode.OK, (await second.PutAsJsonAsync($"/api/admin/users/{adminId}/roles", new { roles = new[] { "ADMIN", "SALES" } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.PutAsJsonAsync($"/api/admin/users/{adminId}/roles", new { roles = new[] { "SALES" } })).StatusCode);
        await AssertFailureAsync(await second.PutAsJsonAsync($"/api/admin/users/{secondId}/roles", new { roles = new[] { "SALES" } }),
            HttpStatusCode.Conflict, AdminUserErrorCodes.LastActiveAdmin);
    }

    [Fact]
    public async Task Technician_with_upcoming_appointments_cannot_be_disabled_deleted_or_lose_the_role()
    {
        using var factory = new AuthApiFactory();
        var admin = Admin(factory);
        var technician = await SeedTechnicianWithUpcomingAppointmentAsync(factory);

        foreach (var response in new[]
                 {
                     await admin.PatchAsJsonAsync($"/api/admin/users/{technician}/status", new { status = "DISABLED" }),
                     await admin.PatchAsJsonAsync($"/api/admin/users/{technician}/status", new { status = "SUSPENDED" }),
                     await admin.DeleteAsync($"/api/admin/users/{technician}"),
                     await admin.PutAsJsonAsync($"/api/admin/users/{technician}/roles", new { roles = new[] { "SALES" } }),
                 })
        {
            var body = await BodyOf(response);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(AdminUserErrorCodes.AccountHasActiveWork, body.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(1, body.GetProperty("error").GetProperty("details").GetProperty("technicianAppointments").GetArrayLength());
        }

        await using var db = factory.CreateDbContext();
        var stored = await db.UserAccounts.SingleAsync(u => u.Id == technician);
        Assert.Equal(UserStatus.Active, stored.Status);
        Assert.Null(stored.DeletedAt);
    }

    [Fact]
    public async Task Sales_with_open_survey_requests_cannot_be_disabled()
    {
        using var factory = new AuthApiFactory();
        var admin = Admin(factory);
        var (sales, _) = await factory.SeedUserClientAsync(RoleCodes.Sales, "Owner");
        await GrantRoleAsync(factory, sales.Id, RoleCodes.Sales);
        var customer = await factory.SeedCustomerWithSiteAsync();
        var preSurvey = await factory.SeedAsync(PreSurveyTestContext.NewPreSurvey(customer.Site.Id, PreSurveyStatus.Submitted));
        var request = PreSurveyTestContext.NewSurveyRequest(preSurvey.Id);
        request.AssignedSaleId = sales.Id;
        request.Status = SurveyRequestStatus.Pending;
        await factory.SeedAsync(request);

        var response = await admin.PatchAsJsonAsync($"/api/admin/users/{sales.Id}/status", new { status = "DISABLED" });
        await AssertFailureAsync(response, HttpStatusCode.Conflict, AdminUserErrorCodes.AccountHasActiveWork);
    }

    [Fact]
    public async Task Soft_delete_keeps_history_blocks_sign_in_and_hides_the_account_from_normal_lists()
    {
        using var factory = new AuthApiFactory { EmailCanDeliver = true };
        var admin = Admin(factory);
        var (userId, access, _) = await ProvisionAndSignInAsync(factory, admin, "leaver@example.com", "CUSTOMER");

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/users/{userId}?reason=left%20company")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/users/{userId}")).StatusCode);  // idempotent

        Assert.Equal(HttpStatusCode.Unauthorized, (await WithToken(factory, access).GetAsync("/api/customers/me/survey-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "leaver@example.com", password = Password })).StatusCode);

        var normal = await DataOf(await admin.GetAsync("/api/admin/users?search=leaver"));
        Assert.Equal(0, normal.GetProperty("totalItems").GetInt32());
        var withDeleted = await DataOf(await admin.GetAsync("/api/admin/users?search=leaver&includeDeleted=true"));
        Assert.NotEqual(JsonValueKind.Null, Assert.Single(withDeleted.GetProperty("items").EnumerateArray()).GetProperty("deletedAt").ValueKind);

        await AssertFailureAsync(await admin.PatchAsJsonAsync($"/api/admin/users/{userId}/status", new { status = "ACTIVE" }),
            HttpStatusCode.Conflict, AdminUserErrorCodes.AccountDeleted);

        await using var db = factory.CreateDbContext();
        Assert.True(await db.UserAccounts.AnyAsync(u => u.Id == userId));            // row kept for references
        Assert.True(await db.UserRoles.AnyAsync(r => r.UserId == userId));            // role history kept
        Assert.Contains(await db.UserAccountAuditEntries.Where(a => a.TargetUserId == userId).Select(a => a.Action).ToListAsync(), a => a == AdminAuditActions.Deleted);
    }

    [Fact]
    public async Task Send_password_reset_issues_the_right_link_without_revealing_it()
    {
        using var factory = new AuthApiFactory { EmailCanDeliver = true };
        var admin = Admin(factory);
        var (activeId, _, _) = await ProvisionAndSignInAsync(factory, admin, "active@example.com", "SALES");
        var pendingId = await CreateAsync(admin, "pending@example.com", "Pending", "SALES");

        var reset = await admin.PostAsync($"/api/admin/users/{activeId}/send-password-reset", null);
        var raw = await reset.Content.ReadAsStringAsync();
        var resetData = await DataOf(reset, HttpStatusCode.Accepted);
        Assert.Equal(ActivationStatuses.PasswordResetIssued, resetData.GetProperty("activation").GetProperty("status").GetString());
        var resetEvent = Assert.Single(factory.Publisher.PublishedOf<PasswordResetRequestedEvent>());
        Assert.DoesNotContain(resetEvent.ResetUrl, raw);

        var setup = await DataOf(await admin.PostAsync($"/api/admin/users/{pendingId}/send-password-reset", null), HttpStatusCode.Accepted);
        Assert.Equal(ActivationStatuses.PendingAccountSetup, setup.GetProperty("activation").GetProperty("status").GetString());

        // Only the newest setup link stays valid.
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.AuthActionTokens.Where(t => t.UserId == pendingId && t.UsedAt == null).ToListAsync());

        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync($"/api/admin/users/{activeId}/status", new { status = "DISABLED" })).StatusCode);
        await AssertFailureAsync(await admin.PostAsync($"/api/admin/users/{activeId}/send-password-reset", null), HttpStatusCode.Conflict, AdminUserErrorCodes.RecoveryNotAllowed);
    }

    private static async Task GrantRoleAsync(AuthApiFactory factory, Guid userId, string role)
    {
        await using var db = factory.CreateDbContext();
        var roleId = (await db.Roles.SingleAsync(r => r.Code == role)).Id;
        if (!await db.UserRoles.AnyAsync(r => r.UserId == userId && r.RoleId == roleId))
        {
            db.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId, AssignedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
    }

    private static async Task<Guid> SeedTechnicianWithUpcomingAppointmentAsync(AuthApiFactory factory)
    {
        var (technician, _) = await factory.SeedUserClientAsync(RoleCodes.Technician, "Busy Tech");
        await GrantRoleAsync(factory, technician.Id, RoleCodes.Technician);
        var customer = await factory.SeedCustomerWithSiteAsync();
        var preSurvey = await factory.SeedAsync(PreSurveyTestContext.NewPreSurvey(customer.Site.Id, PreSurveyStatus.Submitted));
        var request = await factory.SeedAsync(PreSurveyTestContext.NewSurveyRequest(preSurvey.Id));
        var now = DateTimeOffset.UtcNow;
        await factory.SeedAsync(new TechnicianProfile { UserId = technician.Id, IsSchedulingActive = true, CreatedAt = now, UpdatedAt = now });
        await factory.SeedAsync(new TechnicianAppointment
        {
            Id = Guid.NewGuid(), AppointmentType = AppointmentTypes.PreSurvey, SurveyRequestId = request.Id, PropertySiteId = customer.Site.Id,
            StartsAt = now.AddDays(2), EndsAt = now.AddDays(2).AddMinutes(90), ReservedTechnicianUserId = technician.Id,
            Status = AppointmentStatuses.PendingAcceptance, AcceptanceDeadlineAt = now.AddHours(4), BookedByUserId = customer.User.Id, BookedAt = now, CreatedAt = now, UpdatedAt = now,
        });
        return technician.Id;
    }
}
