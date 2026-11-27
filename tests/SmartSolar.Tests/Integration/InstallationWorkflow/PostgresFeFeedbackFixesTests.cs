using SmartSolar.Modules.Common.Paging;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>FE feedback after the Flow 1–4 audit on a disposable PostgreSQL database: values survive a restart (new contexts, new handlers).</summary>
[Collection(WallClockSensitiveCollection.Name)]
public sealed class PostgresFeFeedbackFixesTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [PostgresFact]
    public async Task ISSUE01_after_a_restart_the_quotation_still_points_to_its_30_70_contract_and_the_contract_opens_with_its_plan()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        PerPanelServicePackageTests.World w;
        PaymentPlanFlow.Agreed flow;
        await using (var after = AfterSalesTestContext.CreateOn(database.NewContext()))
            (w, flow) = await PerPanelServicePackageTests.AgreedBundleAsync(on: after);

        // "Restart": the writing context is gone; a brand-new context and brand-new handlers read only what PostgreSQL stored.
        await using var read = database.NewContext();
        var ctx = w.Ctx;
        var quotationId = flow.Cart.Quotation.QuotationId;
        Assert.Equal(flow.ContractId, (await ctx.Quotations(read).GetForTechnicianAsync(flow.TechnicianId, quotationId, None)).Value!.ContractId);
        Assert.Equal(flow.ContractId, Assert.Single((await ctx.Quotations(read).ListForTechnicianAsync(flow.TechnicianId, flow.Cart.RequestId, None)).Value!,
            q => q.QuotationId == quotationId).ContractId);
        var contract = (await ctx.Contracts(read).GetForStaffAsync(flow.TechnicianId, false, flow.ContractId, None)).Value!;
        Assert.Equal(2, contract.PaymentPlan!.Installments.Count);
        Assert.Equal(contract.TotalAmount, contract.PaymentPlan.Installments.Sum(i => i.Amount));
        Assert.NotEmpty(contract.ServicePackages!);
        Assert.True((await ctx.Contracts(read).GetForCustomerAsync(flow.CustomerId, flow.ContractId, None)).Succeeded);
    }

    [PostgresFact]
    public async Task ISSUE02_customer_names_are_batch_resolved_on_postgres_for_the_list_and_the_page()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        var t = await FeFeedbackFixesTests.TwoProjectsAsync(ctx);
        var deleted = await ctx.Db.UserAccounts.FindAsync(t.FirstCustomerId);
        deleted!.DeletedAt = ctx.Clock.GetUtcNow();
        deleted.Status = Modules.Identity.Enums.UserStatus.Disabled;
        await ctx.Db.SaveChangesAsync();

        await using var read = database.NewContext();
        var list = await ctx.Installations(read).ListProjectsAsync(null, None);
        Assert.Equal("Nguyễn Văn An", list.Single(p => p.ProjectId == t.First.ProjectId).CustomerName);   // soft-deleted: stored name kept
        Assert.Equal("Trần Thị Bình", list.Single(p => p.ProjectId == t.Second.ProjectId).CustomerName);
        var page = await ctx.Installations(read).PageProjectsAsync(null, new PageRequest(1, 20), None);
        Assert.Equal(2, page.TotalItems);
        Assert.Equal(list.ToDictionary(p => p.ProjectId, p => p.CustomerName), page.Items.ToDictionary(p => p.ProjectId, p => p.CustomerName));
    }
}
