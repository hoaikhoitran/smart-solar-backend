using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;

namespace SmartSolar.Tests.Unit.AfterSales;

/// <summary>
/// Installation warranty durations: a new contract cannot be issued without one of the Admin's options (clear errors while none are
/// configured), and a legacy contract without recorded months gets understandable answers instead of an invented duration.
/// </summary>
public sealed class WarrantySetupTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_contract_needs_an_admin_warranty_option_before_it_can_be_issued()
    {
        // Arrange: an accepted quotation and no warranty options yet.
        await using var ctx = AfterSalesTestContext.Create();
        var (survey, quotation) = await QuotationTests.IssuedAsync(ctx.Installation);
        Assert.True((await ctx.Installation.Quotations().AcceptAsync(survey.Request.Customer.Id, quotation.QuotationId, None)).Succeeded);
        var contracts = ctx.Installation.Contracts();
        var sales = survey.Sales.Id;

        // Act / Assert: nothing to choose from yet.
        var refused = await contracts.CreateAsync(sales, quotation.QuotationId, "Terms", None, warrantyMonths: 24);
        Assert.Equal(ContractErrorCodes.InvalidWarranty, refused.ErrorCode);
        Assert.Contains("an Admin must add them", refused.Message);
        var draft = (await contracts.CreateAsync(sales, quotation.QuotationId, "Terms", None)).Value!;
        Assert.Null(draft.WarrantyMonths);
        Assert.Equal(ContractErrorCodes.WarrantyRequired, (await contracts.IssueAsync(sales, draft.ContractId, draft.Revision, None)).ErrorCode);

        // The Admin configures the options; Sales picks one on the draft and issues it.
        var admin = await ctx.Installation.Scheduling.Inner.SeedUserAsync("Admin");
        Assert.True((await ctx.Packages().SaveWarrantyOptionAsync(admin.Id, WarrantyOptionKinds.Installation, 60, true, true, None)).Succeeded);
        Assert.Equal(new[] { 60 }, (await ctx.Packages().ListWarrantyOptionsAsync(WarrantyOptionKinds.Installation, None)).Select(o => o.Months));
        Assert.Equal(ContractErrorCodes.InvalidWarranty, (await contracts.UpdateTermsAsync(sales, draft.ContractId, draft.Revision, "Terms", None, warrantyMonths: 36)).ErrorCode);
        var updated = (await contracts.UpdateTermsAsync(sales, draft.ContractId, draft.Revision, "Terms", None, warrantyMonths: 60)).Value!;
        var issued = await contracts.IssueAsync(sales, updated.ContractId, updated.Revision, None);
        Assert.True(issued.Succeeded, issued.Message);
        Assert.Equal(60, issued.Value!.WarrantyMonths);
    }

    [Fact]
    public async Task A_legacy_contract_without_warranty_months_gets_clear_answers_and_is_not_changed()
    {
        // Arrange: a handed-over installation whose contract was signed before warranty options existed.
        await using var ctx = AfterSalesTestContext.Create();
        var h = await ctx.HandedOverAsync();
        await ctx.Db.CustomerContracts.Where(c => c.Id == h.ContractId).ExecuteUpdateAsync(c => c.SetProperty(x => x.WarrantyMonths, (int?)null));
        var before = await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == h.ContractId);

        // Act
        var warranty = await ctx.Customer().GetWarrantyAsync(h.CustomerId, h.ProjectId, None);
        var fault = await ctx.FaultAsync(h);
        var items = await ctx.InspectedAsync(h, fault.RequestId, AfterSalesTestContext.Later(3), new DiagnosedItemInput(null, "Inverter fan", "noisy", null));
        var asWarranty = await ctx.Manager().DecideCoverageAsync(h.ManagerId, fault.RequestId,
            [new CoverageDecision(items[0], ItemCoverages.InstallationWarranty, "customer says it is covered")], None);

        // Assert: no invented duration, a controlled refusal, and the signed contract is untouched.
        Assert.True(warranty.Succeeded);
        Assert.Equal((InstallationWarrantyStatuses.NotRecorded, (int?)null, false), (warranty.Value!.WarrantyStatus, warranty.Value.WarrantyMonths, warranty.Value.UnderWarranty));
        Assert.Null(fault.InstallationWarrantyEndsAt);
        Assert.False(fault.WithinInstallationWarranty);
        Assert.Equal(AfterSalesErrorCodes.InvalidState, asWarranty.ErrorCode);
        Assert.Contains("No installation warranty duration is recorded", asWarranty.Message);
        await ctx.DecideAsync(h, fault.RequestId, (items[0], ItemCoverages.Chargeable));   // the normal paid path still works
        var after = await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == h.ContractId);
        Assert.Equal((before.WarrantyMonths, before.Status, before.CanonicalHash, before.Revision), (after.WarrantyMonths, after.Status, after.CanonicalHash, after.Revision));
    }
}
