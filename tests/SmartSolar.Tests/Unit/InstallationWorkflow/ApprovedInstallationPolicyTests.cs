using Microsoft.EntityFrameworkCore;
using SmartSolar.Api.Controllers;
using SmartSolar.Modules.Common.Time;
using SmartSolar.Modules.Installations;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// Product Owner decisions of 2026-10-31 on installations: D-04 mandatory prerequisites that a note cannot waive (also for projects created
/// before they became mandatory) and a frozen checklist once work started; D-05 installed equipment checked against the accepted quotation,
/// with Manager and customer approval of a substitution before handover. D-10 message time formatting.
/// </summary>
public sealed class ApprovedInstallationPolicyTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_substituted_inverter_needs_the_manager_then_the_customer_before_handover_and_history_is_kept()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, tech, _) = await InstallationTests.ScheduledAsync(ctx);
        var installs = ctx.Installations();
        await InstallationTests.StartAsync(ctx, project.ProjectId, tech);
        await InstallationTests.AddCompletionEvidenceAsync(ctx, project.ProjectId, tech);
        var quotationId = (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == project.ContractId)).QuotationId;
        var lines = await ctx.Db.QuotationLines.AsNoTracking().Where(l => l.QuotationId == quotationId).ToListAsync();
        var panel = lines.Single(l => l.Quantity == 12m).ProductId!.Value;
        var contractedInverter = lines.Single(l => l.Quantity == 1m).ProductId!.Value;
        var otherInverter = await QuotationTests.InverterAsync(ctx, "SUN-OTHER", 14_000_000m);

        // The technician installs the 12 contracted panels but another inverter model.
        var assets = Enumerable.Range(1, 12).Select(i => new AssetInput("PANEL", panel, "Jinko", "Tiger Neo", $"SUB-PNL-{i:00}", null, null))
            .Append(new AssetInput("INVERTER", otherInverter.Id, "Huawei", "SUN-OTHER", "SUB-INV-01", null, null)).ToList();
        Assert.True((await installs.RegisterAssetsAsync(tech, project.ProjectId, assets, None)).Succeeded);
        var staff = (await installs.GetForManagerAsync(project.ProjectId, None)).Value!;
        Assert.Equal(EquipmentComparisonStatuses.ApprovalRequired, staff.Equipment!.Status);
        Assert.Equal(new[] { (contractedInverter, 1m, 0), (otherInverter.Id, 0m, 1) }.OrderBy(x => x.Item1),
            staff.Equipment.Differences.Select(d => (d.ProductId, d.ContractedQuantity, d.InstalledQuantity)).OrderBy(x => x.ProductId));

        // No completion report without the Manager's approval.
        var report = await installs.ReportCompletionAsync(tech, project.ProjectId, "Installed", null, None);
        Assert.Equal(InstallationErrorCodes.SubstitutionApprovalRequired, report.ErrorCode);
        Assert.Equal(409, OperationResultControllerBase.StatusFor(report.ErrorCode!));
        var approved = (await installs.ApproveSubstitutionAsync(paid.ManagerId, project.ProjectId, staff.Revision, "Contracted model out of stock; same rating", None)).Value!;
        Assert.Equal((EquipmentComparisonStatuses.CustomerApprovalRequired, paid.ManagerId, "Contracted model out of stock; same rating"),
            (approved.Equipment!.Status, approved.Equipment.ManagerApproval!.ActorUserId, approved.Equipment.ManagerApproval.Reason));
        // M-3: a structured record bound to the comparison, one item per affected product (not a forced 1:1 pairing).
        var record = await ctx.Db.EquipmentSubstitutions.AsNoTracking().SingleAsync(x => x.ProjectId == project.ProjectId);
        Assert.Equal((EquipmentSubstitutionStatuses.CustomerApprovalPending, approved.Equipment.Fingerprint, quotationId, paid.ManagerId),
            (record.Status, record.Fingerprint, record.QuotationId, record.ManagerUserId));
        var recordItems = await ctx.Db.EquipmentSubstitutionItems.AsNoTracking().Where(i => i.SubstitutionId == record.Id).ToListAsync();
        Assert.Equal(new[] { (contractedInverter, 1m, 0), (otherInverter.Id, 0m, 1) }.OrderBy(x => x.Item1),
            recordItems.Select(i => (i.ProductId!.Value, i.ContractedQuantity, i.InstalledQuantity)).OrderBy(x => x.Item1));
        Assert.True((await installs.ReportCompletionAsync(tech, project.ProjectId, "Installed", null, None)).Succeeded);

        // A Manager cannot agree on the customer's behalf (the customer action is scoped to the project's customer).
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await installs.CustomerApproveSubstitutionAsync(paid.ManagerId, project.ProjectId, None)).ErrorCode);

        // No handover until the customer agrees; another customer cannot agree for them; staff identity is not shown to the customer.
        var accept = await installs.DecideAcceptanceAsync(paid.CustomerId, project.ProjectId, true, null, None);
        Assert.Equal(InstallationErrorCodes.SubstitutionCustomerApprovalRequired, accept.ErrorCode);
        var stranger = await ctx.Scheduling.Inner.SeedUserAsync("Stranger");
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await installs.CustomerApproveSubstitutionAsync(stranger.Id, project.ProjectId, None)).ErrorCode);
        var customerView = (await installs.GetForCustomerAsync(paid.CustomerId, project.ProjectId, None)).Value!;
        Assert.Equal(EquipmentComparisonStatuses.CustomerApprovalRequired, customerView.Equipment!.Status);
        Assert.Null(customerView.Equipment.ManagerApproval!.ActorUserId);
        Assert.Equal("Contracted model out of stock; same rating", customerView.Equipment.ManagerApproval.Reason);
        var agreed = (await installs.CustomerApproveSubstitutionAsync(paid.CustomerId, project.ProjectId, None)).Value!;
        Assert.Equal(EquipmentComparisonStatuses.Approved, agreed.Equipment!.Status);
        Assert.True((await installs.CustomerApproveSubstitutionAsync(paid.CustomerId, project.ProjectId, None)).Succeeded);   // repeat: no-op
        var completed = (await installs.DecideAcceptanceAsync(paid.CustomerId, project.ProjectId, true, null, None)).Value!;
        Assert.Equal(InstallationStatuses.Completed, completed.Status);
        var final = await ctx.Db.EquipmentSubstitutions.AsNoTracking().SingleAsync(x => x.ProjectId == project.ProjectId);
        Assert.Equal((EquipmentSubstitutionStatuses.Approved, (Guid?)paid.CustomerId), (final.Status, final.CustomerUserId));

        // History: the contracted quotation is unchanged, both approvals are audited once, the installed assets are what was installed.
        Assert.Equal(1m, (await ctx.Db.QuotationLines.AsNoTracking().SingleAsync(l => l.QuotationId == quotationId && l.ProductId == contractedInverter)).Quantity);
        var events = await ctx.Db.InstallationEvents.AsNoTracking().Where(e => e.ProjectId == project.ProjectId).ToListAsync();
        Assert.Single(events, e => e.EventType == InstallationEventTypes.SubstitutionApproved);
        Assert.Single(events, e => e.EventType == InstallationEventTypes.SubstitutionCustomerApproved && e.ActorUserId == paid.CustomerId);
        Assert.Equal(otherInverter.Id, (await ctx.Db.InstalledAssets.AsNoTracking().SingleAsync(a => a.ProjectId == project.ProjectId && a.AssetType == "INVERTER")).ProductId);
    }

    [Fact]
    public async Task A_customer_refusal_or_an_equipment_change_ends_the_approval_and_the_gates_close_again_with_history_kept()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, tech, _) = await InstallationTests.ScheduledAsync(ctx);
        var installs = ctx.Installations();
        await InstallationTests.StartAsync(ctx, project.ProjectId, tech);
        await InstallationTests.AddCompletionEvidenceAsync(ctx, project.ProjectId, tech);
        // 11 panels for 12 contracted, plus the inverter: a quantity difference.
        var quotationId = (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == project.ContractId)).QuotationId;
        var lines = await ctx.Db.QuotationLines.AsNoTracking().Where(l => l.QuotationId == quotationId).ToListAsync();
        var panel = lines.Single(l => l.Quantity == 12m).ProductId!.Value;
        var inverter = lines.Single(l => l.Quantity == 1m).ProductId!.Value;
        Assert.True((await installs.RegisterAssetsAsync(tech, project.ProjectId, Enumerable.Range(1, 11)
            .Select(i => new AssetInput("PANEL", panel, "Jinko", "Tiger Neo", $"Q-PNL-{i:00}", null, null))
            .Append(new AssetInput("INVERTER", inverter, "Huawei", "SUN", "Q-INV-01", null, null)).ToList(), None)).Succeeded);
        var revision = (await installs.GetForManagerAsync(project.ProjectId, None)).Value!.Revision;
        var first = (await installs.ApproveSubstitutionAsync(paid.ManagerId, project.ProjectId, revision, "One panel damaged in transport", None)).Value!;
        Assert.Equal((12m, 11), (Assert.Single(first.Equipment!.Differences).ContractedQuantity, first.Equipment.Differences[0].InstalledQuantity));
        // Duplicate approval of the same comparison: refused, still one record.
        Assert.Equal(InstallationErrorCodes.InvalidState, (await installs.ApproveSubstitutionAsync(paid.ManagerId, project.ProjectId, first.Revision, "again", None)).ErrorCode);
        Assert.Single(await ctx.Db.EquipmentSubstitutions.AsNoTracking().Where(x => x.ProjectId == project.ProjectId).ToListAsync());
        Assert.True((await installs.ReportCompletionAsync(tech, project.ProjectId, "Installed", null, None)).Succeeded);

        // The customer asks for rework instead of agreeing: the record ends as CUSTOMER_DECLINED with the reason.
        Assert.True((await installs.DecideAcceptanceAsync(paid.CustomerId, project.ProjectId, false, "I paid for 12 panels", None)).Succeeded);
        var declined = await ctx.Db.EquipmentSubstitutions.AsNoTracking().SingleAsync(x => x.ProjectId == project.ProjectId);
        Assert.Equal((EquipmentSubstitutionStatuses.CustomerDeclined, (Guid?)paid.CustomerId, "I paid for 12 panels"),
            (declined.Status, declined.CustomerUserId, declined.CustomerDeclineReason));
        Assert.Equal(EquipmentComparisonStatuses.ApprovalRequired, (await installs.GetForManagerAsync(project.ProjectId, None)).Value!.Equipment!.Status);

        // A Manager approves again, then the 12th panel is installed: the new approval is superseded (equipment changed) and the comparison matches.
        revision = (await installs.GetForManagerAsync(project.ProjectId, None)).Value!.Revision;
        Assert.True((await installs.ApproveSubstitutionAsync(paid.ManagerId, project.ProjectId, revision, "Replacement panel on back-order", None)).Succeeded);
        Assert.True((await installs.RegisterAssetsAsync(tech, project.ProjectId, [new AssetInput("PANEL", panel, "Jinko", "Tiger Neo", "Q-PNL-12", null, null)], None)).Succeeded);
        var records = (await ctx.Db.EquipmentSubstitutions.AsNoTracking().Where(x => x.ProjectId == project.ProjectId).ToListAsync()).OrderBy(x => x.CreatedAt).ThenBy(x => x.Status).ToList();
        Assert.Equal(new[] { EquipmentSubstitutionStatuses.CustomerDeclined, EquipmentSubstitutionStatuses.Superseded }.Order(), records.Select(x => x.Status).Order());
        var staff = (await installs.GetForManagerAsync(project.ProjectId, None)).Value!;
        Assert.Equal(EquipmentComparisonStatuses.Matches, staff.Equipment!.Status);
        Assert.Equal(2, staff.Equipment.History!.Count);   // the history stays visible to staff

        // The database allows at most one active substitution per project.
        var stale = records[0];
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            for (var i = 0; i < 2; i++)
                ctx.Db.EquipmentSubstitutions.Add(new EquipmentSubstitution
                {
                    Id = Guid.NewGuid(), ProjectId = project.ProjectId, ContractId = project.ContractId, QuotationId = quotationId, Fingerprint = new string('a', 32),
                    Status = EquipmentSubstitutionStatuses.CustomerApprovalPending, ManagerUserId = paid.ManagerId, ManagerReason = "x",
                    ManagerApprovedAt = stale.ManagerApprovedAt, CreatedAt = stale.CreatedAt, UpdatedAt = stale.CreatedAt,
                });
            await ctx.Db.SaveChangesAsync();
        });
        ctx.Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task An_installation_matching_the_contract_needs_no_substitution_approval()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, tech, _) = await InstallationTests.ScheduledAsync(ctx);
        var installs = ctx.Installations();
        await InstallationTests.StartAsync(ctx, project.ProjectId, tech);
        await InstallationTests.AddCompletionEvidenceAsync(ctx, project.ProjectId, tech);
        Assert.True((await InstallationTests.RegisterPanelAndInverterAsync(ctx, project.ProjectId, tech)).Succeeded);
        var staff = (await installs.GetForManagerAsync(project.ProjectId, None)).Value!;
        Assert.Equal((EquipmentComparisonStatuses.Matches, 0), (staff.Equipment!.Status, staff.Equipment.Differences.Count));
        Assert.Equal(InstallationErrorCodes.NoSubstitution, (await installs.ApproveSubstitutionAsync(paid.ManagerId, project.ProjectId, staff.Revision, "n/a", None)).ErrorCode);
        Assert.True((await installs.ReportCompletionAsync(tech, project.ProjectId, "Installed", null, None)).Succeeded);
        Assert.Equal(InstallationErrorCodes.NoSubstitution, (await installs.CustomerApproveSubstitutionAsync(paid.CustomerId, project.ProjectId, None)).ErrorCode);
        Assert.True((await installs.DecideAcceptanceAsync(paid.CustomerId, project.ProjectId, true, null, None)).Succeeded);
    }

    [Fact]
    public async Task A_completion_reported_before_the_check_can_still_be_approved_and_a_completed_legacy_project_is_shown_as_history()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, tech, _) = await InstallationTests.ScheduledAsync(ctx);
        var installs = ctx.Installations();
        await InstallationTests.StartAsync(ctx, project.ProjectId, tech);
        await InstallationTests.AddCompletionEvidenceAsync(ctx, project.ProjectId, tech);
        Assert.True((await InstallationTests.RegisterPanelAndInverterAsync(ctx, project.ProjectId, tech)).Succeeded);
        Assert.True((await installs.ReportCompletionAsync(tech, project.ProjectId, "Installed", null, None)).Succeeded);

        // An inverter registered before assets had to reference the catalog (legacy data, written directly).
        var legacy = await ctx.Db.InstalledAssets.SingleAsync(a => a.ProjectId == project.ProjectId && a.AssetType == "INVERTER");
        legacy.ProductId = null;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();

        // The customer cannot accept yet; the Manager can still approve while the acceptance is open, then the customer agrees.
        Assert.Equal(InstallationErrorCodes.SubstitutionCustomerApprovalRequired, (await installs.DecideAcceptanceAsync(paid.CustomerId, project.ProjectId, true, null, None)).ErrorCode);
        var pending = (await installs.GetForManagerAsync(project.ProjectId, None)).Value!;
        Assert.True((await installs.ApproveSubstitutionAsync(paid.ManagerId, project.ProjectId, pending.Revision, "Legacy registration without catalog link", None)).Succeeded);
        Assert.True((await installs.CustomerApproveSubstitutionAsync(paid.CustomerId, project.ProjectId, None)).Succeeded);
        Assert.True((await installs.DecideAcceptanceAsync(paid.CustomerId, project.ProjectId, true, null, None)).Succeeded);
        Assert.Equal(InstallationErrorCodes.InvalidState,
            (await installs.ApproveSubstitutionAsync(paid.ManagerId, project.ProjectId, (await installs.GetForManagerAsync(project.ProjectId, None)).Value!.Revision, "late", None)).ErrorCode);

        // A project handed over before the check, with a difference nobody approved, reads as history, not as a pending requirement.
        var ctx2 = InstallationWorkflowTestContext.Create();
        await using var __ = ctx2;
        var (paid2, project2, tech2, _) = await InstallationTests.ScheduledAsync(ctx2);
        var installs2 = ctx2.Installations();
        await InstallationTests.StartAsync(ctx2, project2.ProjectId, tech2);
        await InstallationTests.AddCompletionEvidenceAsync(ctx2, project2.ProjectId, tech2);
        Assert.True((await InstallationTests.RegisterPanelAndInverterAsync(ctx2, project2.ProjectId, tech2)).Succeeded);
        Assert.True((await installs2.ReportCompletionAsync(tech2, project2.ProjectId, "Installed", null, None)).Succeeded);
        Assert.True((await installs2.DecideAcceptanceAsync(paid2.CustomerId, project2.ProjectId, true, null, None)).Succeeded);
        var old = await ctx2.Db.InstalledAssets.SingleAsync(a => a.ProjectId == project2.ProjectId && a.AssetType == "INVERTER");
        old.ProductId = null;
        await ctx2.Db.SaveChangesAsync();
        ctx2.Db.ChangeTracker.Clear();
        Assert.Equal(EquipmentComparisonStatuses.NotCheckedAtHandover, (await installs2.GetForManagerAsync(project2.ProjectId, None)).Value!.Equipment!.Status);
        Assert.Equal(InstallationStatuses.Completed, (await installs2.GetForCustomerAsync(paid2.CustomerId, project2.ProjectId, None)).Value!.Status);
    }

    [Fact]
    public async Task Mandatory_prerequisites_block_even_a_legacy_project_waived_by_note_and_the_checklist_freezes_once_work_started()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, tech, _) = await InstallationTests.ScheduledAsync(ctx);
        var installs = ctx.Installations();

        // A project created before the prerequisites became mandatory (not required), or waived with a note only.
        var items = await ctx.Db.InstallationChecklistItems.Where(i => i.ProjectId == project.ProjectId).ToListAsync();
        items.Single(i => i.Code == "GRID_CONNECTION_APPROVAL").IsRequired = false;
        var fire = items.Single(i => i.Code == "FIRE_SAFETY_APPROVAL");
        fire.Status = ChecklistStatuses.NotApplicable;
        fire.Note = "Manager: not needed";
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();

        ctx.Clock.Now = SchedulingTestContext.At(8, 30, SchedulingTestContext.Day.AddDays(1)).ToUniversalTime();
        var view = (await installs.GetForTechnicianAsync(tech, project.ProjectId, None)).Value!;
        Assert.Equal((false, InstallationErrorCodes.ChecklistIncomplete), (view.CanStart, view.StartBlockedReason));
        var refused = await installs.StartProjectAsync(tech, project.ProjectId, None);
        Assert.Equal(InstallationErrorCodes.ChecklistIncomplete, refused.ErrorCode);
        var blocking = System.Text.Json.JsonSerializer.Serialize(refused.Details);
        Assert.Contains("GRID_CONNECTION_APPROVAL", blocking);
        Assert.Contains("FIRE_SAFETY_APPROVAL", blocking);
        Assert.Contains("STRUCTURAL_ASSESSMENT", blocking);

        // Satisfying all three with verified documents lets work start; afterwards the checklist is history.
        await InstallationTests.StartAsync(ctx, project.ProjectId, tech);
        var started = (await installs.GetForManagerAsync(project.ProjectId, None)).Value!;
        Assert.Equal(InstallationStatuses.InProgress, started.Status);
        var frozen = await installs.UpdateChecklistItemAsync(paid.ManagerId, project.ProjectId, started.Checklist[0].ItemId, started.Revision,
            new ChecklistUpdate(true, ChecklistStatuses.Pending, null, "Reopen"), None);
        Assert.Equal(InstallationErrorCodes.InvalidState, frozen.ErrorCode);
        Assert.All((await installs.GetForManagerAsync(project.ProjectId, None)).Value!.Checklist, i => Assert.Equal(ChecklistStatuses.Satisfied, i.Status));
    }

    // ------------------------------------------------------------------ D-10

    [Theory]
    [InlineData("2026-10-20T03:00:00Z", "10:00 20/10/2026 (GMT+07:00)")]
    [InlineData("2026-10-31T17:00:00Z", "00:00 01/11/2026 (GMT+07:00)")]   // month boundary in Vietnam, still October in UTC
    [InlineData("2026-12-31T17:30:00Z", "00:30 01/01/2027 (GMT+07:00)")]   // year boundary
    [InlineData("2027-02-28T16:59:00Z", "23:59 28/02/2027 (GMT+07:00)")]
    public void Message_times_are_written_in_the_business_time_zone_with_the_offset(string utc, string expected)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
        Assert.Equal(expected, DisplayTime.Format(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture), zone));
        // The same instant with another offset formats identically: only the instant matters.
        Assert.Equal(expected, DisplayTime.Format(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture).ToOffset(TimeSpan.FromHours(-5)), zone));
    }
}
