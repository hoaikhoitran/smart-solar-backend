using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Common.Paging;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Installations;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// FE feedback after the Flow 1–4 audit (2026-11-25): ISSUE-01 the 30/70 contract id after AGREED (bundled per-panel packages, contract detail with its
/// installment plan, SUPERSEDED), ISSUE-02 the customer's display name on the Manager installation project list.
/// </summary>
public sealed class FeFeedbackFixesTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    // ------------------------------------------------------------------ ISSUE-01

    [Fact]
    public async Task ISSUE01_agreed_with_bundled_per_panel_packages_returns_a_contract_that_opens_with_its_30_70_plan()
    {
        var (w, flow) = await PerPanelServicePackageTests.AgreedBundleAsync(withMaintenance: true);
        var ctx = w.Ctx;
        await using var _ = w.After;
        var quotationId = flow.Cart.Quotation.QuotationId;

        // The AGREED response carried the id (AgreeAsync asserts it); every later read returns the same id.
        Assert.Equal(flow.ContractId, flow.Cart.Quotation.ContractId);
        Assert.Equal(flow.ContractId, (await ctx.Quotations().GetForTechnicianAsync(flow.TechnicianId, quotationId, None)).Value!.ContractId);
        Assert.Equal(flow.ContractId, Assert.Single((await ctx.Quotations().ListForTechnicianAsync(flow.TechnicianId, flow.Cart.RequestId, None)).Value!,
            q => q.QuotationId == quotationId).ContractId);

        // That id opens the contract (staff/technician read and customer read) with the 30/70 plan and the bundled packages.
        var contract = (await ctx.Contracts().GetForStaffAsync(flow.TechnicianId, false, flow.ContractId, None)).Value!;
        Assert.Equal(quotationId, contract.QuotationId);
        var plan = contract.PaymentPlan!;
        Assert.Equal(2, plan.Installments.Count);
        Assert.Equal([30m, 70m], plan.Installments.OrderBy(i => i.Sequence).Select(i => i.Percent));
        Assert.Equal(contract.TotalAmount, plan.Installments.Sum(i => i.Amount));
        Assert.Equal(Math.Round(contract.TotalAmount * 0.30m, 0), plan.Installments.Single(i => i.Sequence == 1).Amount);   // 30 % of the FULL total
        Assert.False(plan.DepositPaid);
        Assert.Equal(0m, plan.Paid);
        Assert.NotEmpty(contract.ServicePackages!);
        Assert.Equal(contract.TotalAmount, contract.InstallationTotal + contract.ServicePackagesTotal);
        var customerView = await ctx.Contracts().GetForCustomerAsync(flow.CustomerId, flow.ContractId, None);
        Assert.True(customerView.Succeeded, customerView.Message);
        Assert.Equal(2, customerView.Value!.PaymentPlan!.Installments.Count);

        // A fresh handler on a fresh unit of work (nothing kept in memory) still resolves it.
        Assert.Equal(flow.ContractId, (await ctx.QuotationsWithPaymentPlan().GetForTechnicianAsync(flow.TechnicianId, quotationId, None)).Value!.ContractId);
    }

    [Fact]
    public async Task ISSUE01_a_superseded_contract_is_not_reported_and_a_live_version_is_reported_again()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var flow = await PaymentPlanFlow.AgreedAsync(ctx);
        var quotationId = flow.Cart.Quotation.QuotationId;

        // Synthetic history: the only contract version marked SUPERSEDED → nothing live to open.
        var row = await ctx.Db.CustomerContracts.SingleAsync(c => c.Id == flow.ContractId);
        row.Status = ContractStatuses.Superseded;
        row.CanonicalHash ??= new string('a', 64);   // an issued (then superseded) version always carries its hash and issue time
        row.IssuedAt ??= ctx.Clock.GetUtcNow();
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        Assert.Null((await ctx.Quotations().GetForTechnicianAsync(flow.TechnicianId, quotationId, None)).Value!.ContractId);
        Assert.Null(Assert.Single((await ctx.Quotations().ListForTechnicianAsync(flow.TechnicianId, flow.Cart.RequestId, None)).Value!).ContractId);

        // Restored as the live version: reported again.
        row = await ctx.Db.CustomerContracts.SingleAsync(c => c.Id == flow.ContractId);
        row.Status = ContractStatuses.Draft;
        await ctx.Db.SaveChangesAsync();
        Assert.Equal(flow.ContractId, (await ctx.Quotations().GetForTechnicianAsync(flow.TechnicianId, quotationId, None)).Value!.ContractId);
    }

    // ------------------------------------------------------------------ ISSUE-02

    internal sealed record TwoProjects(InstallationWorkflowTestContext Ctx, Guid ManagerId, InstallationStaffView First, InstallationStaffView Second,
        Guid FirstCustomerId, Guid SecondCustomerId);

    /// <summary>Two PAID contracts of two different customers, each with its installation project; customers renamed to distinct display names.</summary>
    internal static async Task<TwoProjects> TwoProjectsAsync(InstallationWorkflowTestContext ctx, string firstName = "Nguyễn Văn An", string secondName = "Trần Thị Bình")
    {
        ctx.Clock.Now = SchedulingTestContext.DefaultNow.ToUniversalTime();   // each journey starts at the fixture's "now" (survey booking horizon)
        var a = await InstallationTests.PaidContractAsync(ctx);
        ctx.Clock.Now = SchedulingTestContext.DefaultNow.ToUniversalTime();
        var b = await InstallationTests.PaidContractAsync(ctx);
        Assert.NotEqual(a.CustomerId, b.CustomerId);
        var first = (await ctx.Installations().CreateProjectAsync(a.ManagerId, a.ContractId, None)).Value!;
        var second = (await ctx.Installations().CreateProjectAsync(b.ManagerId, b.ContractId, None)).Value!;
        foreach (var (id, name) in new[] { (a.CustomerId, firstName), (b.CustomerId, secondName) })
            (await ctx.Db.UserAccounts.SingleAsync(u => u.Id == id)).FullName = name;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        return new TwoProjects(ctx, a.ManagerId, first, second, a.CustomerId, b.CustomerId);
    }

    [Fact]
    public async Task ISSUE02_list_and_pages_carry_each_projects_own_customer_name_and_keep_every_existing_field()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var t = await TwoProjectsAsync(ctx);

        var list = await ctx.Installations().ListProjectsAsync(null, None);
        Assert.Equal(2, list.Count);
        Assert.Equal("Nguyễn Văn An", list.Single(p => p.ProjectId == t.First.ProjectId).CustomerName);
        Assert.Equal("Trần Thị Bình", list.Single(p => p.ProjectId == t.Second.ProjectId).CustomerName);
        foreach (var project in new[] { t.First, t.Second })
        {
            var row = list.Single(p => p.ProjectId == project.ProjectId);
            var staff = (await ctx.Installations().GetForManagerAsync(project.ProjectId, None)).Value!;
            Assert.Equal(staff.ContractId, row.ContractId);
            Assert.Equal(staff.Status, row.Status);
            Assert.Equal(staff.Revision, row.Revision);
            Assert.Equal(staff.Zone?.ZoneId, row.OperationalZoneId);
            Assert.Null(row.ResponsibleTechnicianUserId);
            Assert.Null(row.ResponsibleTechnicianName);
            Assert.False(row.TechnicianAccepted);
            Assert.Equal(staff.Site!.CustomerName, row.CustomerName);   // same source as the detail view
        }

        // Paged: one per page, each page names its own customer; the status filter still applies.
        var page1 = await ctx.Installations().PageProjectsAsync(null, new PageRequest(1, 1), None);
        var page2 = await ctx.Installations().PageProjectsAsync(null, new PageRequest(2, 1), None);
        Assert.Equal(2, page1.TotalItems);
        var one = Assert.Single(page1.Items);
        var two = Assert.Single(page2.Items);
        Assert.NotEqual(one.ProjectId, two.ProjectId);
        Assert.Equal(list.Single(p => p.ProjectId == one.ProjectId).CustomerName, one.CustomerName);
        Assert.Equal(list.Single(p => p.ProjectId == two.ProjectId).CustomerName, two.CustomerName);
        Assert.Equal(2, (await ctx.Installations().ListProjectsAsync(t.First.Status, None)).Count(p => p.CustomerName != ""));
        Assert.Empty(await ctx.Installations().ListProjectsAsync(InstallationStatuses.Completed, None));
    }

    [Fact]
    public async Task ISSUE02_an_empty_list_and_an_empty_page_answer_empty()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        Assert.Empty(await ctx.Installations().ListProjectsAsync(null, None));
        var page = await ctx.Installations().PageProjectsAsync(null, new PageRequest(1, 20), None);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalItems);
    }

    [Fact]
    public async Task ISSUE02_a_soft_deleted_customer_keeps_the_stored_name_and_a_missing_account_gives_an_empty_name()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var t = await TwoProjectsAsync(ctx);

        // Soft delete (admin delete keeps the row for history): the stored name is still shown.
        var deleted = await ctx.Db.UserAccounts.SingleAsync(u => u.Id == t.FirstCustomerId);
        deleted.DeletedAt = ctx.Clock.GetUtcNow();
        deleted.Status = UserStatus.Disabled;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        var list = await ctx.Installations().ListProjectsAsync(null, None);
        Assert.Equal("Nguyễn Văn An", list.Single(p => p.ProjectId == t.First.ProjectId).CustomerName);

        // A project whose customer account no longer exists (synthetic orphan row): "" — the other row is unaffected, nothing fails.
        await ctx.Db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
        try
        {
            var project = await ctx.Db.InstallationProjects.SingleAsync(p => p.Id == t.Second.ProjectId);
            project.CustomerUserId = Guid.NewGuid();
            await ctx.Db.SaveChangesAsync();
            ctx.Db.ChangeTracker.Clear();
            list = await ctx.Installations().ListProjectsAsync(null, None);
            Assert.Equal("", list.Single(p => p.ProjectId == t.Second.ProjectId).CustomerName);
            Assert.Equal("Nguyễn Văn An", list.Single(p => p.ProjectId == t.First.ProjectId).CustomerName);
            var page = await ctx.Installations().PageProjectsAsync(null, new PageRequest(1, 20), None);
            Assert.Equal("", page.Items.Single(p => p.ProjectId == t.Second.ProjectId).CustomerName);
        }
        finally
        {
            await ctx.Db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
        }
    }

    [Fact]
    public async Task ISSUE02_names_are_resolved_with_one_lookup_per_list_and_technician_names_are_unchanged()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, scheduled, technician, _) = await InstallationTests.ScheduledAsync(ctx);
        var t = await TwoProjectsAsync(ctx);   // three projects, three customers

        var calls = new List<string>();
        IReadOnlyCollection<Guid> looked = [];
        var unitOfWork = InterceptingProxy<IInstallationUnitOfWork>.Create(new Infrastructure.Persistence.InstallationWorkflow.InstallationUnitOfWork(ctx.Db),
            before: (method, args) =>
            {
                calls.Add(method.Name);
                if (method.Name == nameof(IInstallationUnitOfWork.GetUserNamesAsync)) looked = (IReadOnlyCollection<Guid>)args[0]!;
            });
        var scheduling = ctx.SchedulingWithInstallations();
        var handlers = new InstallationHandlers(unitOfWork, scheduling, new InstallationSchedulePlanner(ctx.Scheduling.Eligibility(scheduling), ctx.Scheduling.Calendar,
            ctx.InstallationOptions, ctx.Scheduling.Assignments(scheduling)), ctx.Files(), ctx.Scheduling.Calendar, ctx.InstallationOptions, ctx.Clock, ctx.Ledger());

        var list = await handlers.ListProjectsAsync(null, None);
        Assert.Equal(3, list.Count);
        // No N+1: one list query and one name lookup for every row (customers and technicians together).
        Assert.Equal([nameof(IInstallationUnitOfWork.ListProjectsAsync), nameof(IInstallationUnitOfWork.GetUserNamesAsync)], calls);
        Assert.Equal(4, looked.Count);
        Assert.Contains(paid.CustomerId, looked);
        Assert.Contains(technician, looked);
        var row = list.Single(p => p.ProjectId == scheduled.ProjectId);
        Assert.Equal(technician, row.ResponsibleTechnicianUserId);
        Assert.Equal(scheduled.ResponsibleTechnician!.FullName, row.ResponsibleTechnicianName);
        Assert.True(row.TechnicianAccepted);
        Assert.Equal(scheduled.Site!.CustomerName, row.CustomerName);
        Assert.NotEqual("", row.CustomerName);
        Assert.Equal("Trần Thị Bình", list.Single(p => p.ProjectId == t.Second.ProjectId).CustomerName);

        calls.Clear();
        var page = await handlers.PageProjectsAsync(null, new PageRequest(1, 2), None);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal([nameof(IInstallationUnitOfWork.PageProjectsAsync), nameof(IInstallationUnitOfWork.GetUserNamesAsync)], calls);
    }
}
