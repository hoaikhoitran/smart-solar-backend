using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.AfterSales;
using SmartSolar.Infrastructure.Persistence.InstallationWorkflow;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.TestSupport;

/// <summary>An installation handed over to its customer through the real Flow 1 and Flow 2 handlers (warranty: 60 months).</summary>
public sealed record HandedOver(Guid CustomerId, Guid ContractId, Guid ProjectId, Guid TechnicianId, Guid ManagerId, Guid SalesId, Guid AdminId,
    DateTimeOffset HandoverAt, Guid InverterId);

/// <summary>
/// Flow 3 (after-sales) handlers wired exactly like <c>AddAfterSales</c> over the Installation Flow test context (SQLite, fixed
/// clock, fake PayOS). <see cref="HandedOverAsync"/> gives a handed-over installation to start from; the small helpers below move a
/// request through the common steps so each test can focus on one rule.
/// </summary>
public sealed class AfterSalesTestContext : IAsyncDisposable
{
    public const int InstallationWarrantyMonths = 60;
    public const int RepairWarrantyMonths = 6;
    private static readonly CancellationToken None = CancellationToken.None;

    private AfterSalesTestContext(InstallationWorkflowTestContext installation) => Installation = installation;

    public static AfterSalesTestContext Create() => new(InstallationWorkflowTestContext.Create());

    public static AfterSalesTestContext CreateOn(AppDbContext db) => new(InstallationWorkflowTestContext.CreateOn(db));

    public InstallationWorkflowTestContext Installation { get; }
    public AppDbContext Db => Installation.Db;
    public FixedTimeProvider Clock => Installation.Clock;

    // ------------------------------------------------------------------ handlers (same wiring as AfterSalesModuleExtensions)

    public AfterSalesUnitOfWork Data(AppDbContext? db = null) => new(db ?? Db);
    private AfterSalesViews Views(AfterSalesUnitOfWork data) => new(data, Installation.Scheduling.Calendar);
    private SubscriptionTerms Terms(AfterSalesUnitOfWork data) => new(data, Installation.Scheduling.Policies, Installation.Scheduling.Calendar);

    /// <summary>PaymentService with the after-sales settlement hook (verified payments apply package and quotation lines).</summary>
    public PaymentService Payments(AppDbContext? db = null)
    {
        var data = Data(db);
        return new(new PaymentUnitOfWork(db ?? Db), Installation.PayOs.Gateway(), Installation.PaymentOptions, Clock, NullLogger<PaymentService>.Instance,
            new AfterSalesPaymentSettlement(data, Terms(data)));
    }

    public ServiceCustomerHandlers Customer(AppDbContext? db = null)
    {
        var data = Data(db);
        return new(data, Installation.Files(db), Installation.Scheduling.Policies, Installation.Scheduling.Calendar, Views(data), Clock);
    }

    public ServiceManagerHandlers Manager(AppDbContext? db = null)
    {
        var data = Data(db);
        var scheduling = Installation.SchedulingWithInstallations(db);
        var planner = new InstallationSchedulePlanner(Installation.Scheduling.Eligibility(scheduling), Installation.Scheduling.Calendar,
            Installation.InstallationOptions, Installation.Scheduling.Assignments(scheduling));
        return new(data, scheduling, planner, Installation.Scheduling.Policies, Views(data), Clock);
    }

    public TechnicianServiceHandlers Technician(AppDbContext? db = null) => new(Data(db), Installation.Files(db), Installation.Scheduling.Calendar, Clock);

    public ServiceQuotationHandlers Quotations(AppDbContext? db = null)
    {
        var data = Data(db);
        return new(data, Payments(db), Installation.Scheduling.Policies, Views(data), Clock);
    }

    public ServicePackageHandlers Packages(AppDbContext? db = null)
    {
        var data = Data(db);
        return new(data, Payments(db), Installation.Scheduling.Policies, Views(data), Clock);
    }

    public SalesTaskHandlers Tasks(AppDbContext? db = null)
    {
        var data = Data(db);
        return new(data, Views(data), Clock);
    }

    public RefundHandlers Refunds()
    {
        var data = Data();
        return new(data, Views(data), Clock);
    }

    public AfterSalesSweepService Sweep()
    {
        var data = Data();
        var terms = Terms(data);
        return new(data, Payments(), terms, Packages(), Installation.Scheduling.Policies, Clock, NullLogger<AfterSalesSweepService>.Instance);
    }

    public AfterSalesDashboard Dashboard() => new(new AfterSalesReportQueries(Db), Installation.Scheduling.Calendar, Clock);

    // ------------------------------------------------------------------ setup

    /// <summary>
    /// Admin warranty options (installation 60 months default, repair 6 months default), then Flow 1 → paid contract → project →
    /// installation → customer acceptance. The contract snapshots the default installation warranty when Sales creates it.
    /// </summary>
    public async Task<HandedOver> HandedOverAsync()
    {
        var admin = await Installation.Scheduling.Inner.SeedUserAsync("Admin");
        Assert.True((await Packages().SaveWarrantyOptionAsync(admin.Id, WarrantyOptionKinds.Installation, InstallationWarrantyMonths, true, true, None)).Succeeded);
        Assert.True((await Packages().SaveWarrantyOptionAsync(admin.Id, WarrantyOptionKinds.Repair, RepairWarrantyMonths, true, true, None)).Succeeded);
        var paid = await InstallationTests.PaidContractAsync(Installation);
        return await HandOverAsync(paid, admin.Id);
    }

    /// <summary>Flow 2 from a PAID contract: Wed 08:00–17:00 installation, started, evidence + panels + inverter, reported, accepted.</summary>
    public async Task<HandedOver> HandOverAsync(InstallationTests.Paid paid, Guid adminId)
    {
        var installs = Installation.Installations();
        var technician = await Installation.Scheduling.SeedTechnicianAsync("Installer", BinhTan);
        var project = (await installs.CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;
        var wed = Day.AddDays(1);
        Assert.True((await installs.AssignAsync(paid.ManagerId, project.ProjectId, new AssignTechnicianInput(technician.Id, At(8, 0, wed), At(17, 0, wed), null, project.Revision), None)).Succeeded);
        Assert.True((await installs.AcceptAssignmentAsync(technician.Id, project.ProjectId, null, None)).Succeeded);
        await InstallationTests.StartAsync(Installation, project.ProjectId, technician.Id);
        await InstallationTests.AddCompletionEvidenceAsync(Installation, project.ProjectId, technician.Id);
        Assert.True((await InstallationTests.RegisterPanelAndInverterAsync(Installation, project.ProjectId, technician.Id)).Succeeded);
        Assert.True((await installs.ReportCompletionAsync(technician.Id, project.ProjectId, "Done", null, None)).Succeeded);
        var accepted = await installs.DecideAcceptanceAsync(paid.CustomerId, project.ProjectId, true, null, None);
        Assert.True(accepted.Succeeded, accepted.Message);
        var sales = await Installation.Scheduling.SeedSalesAsync("After-sales Sales");
        var inverter = await Db.InstalledAssets.AsNoTracking().SingleAsync(a => a.ProjectId == project.ProjectId && a.AssetType == "INVERTER");
        Db.ChangeTracker.Clear();
        return new HandedOver(paid.CustomerId, paid.ContractId, project.ProjectId, technician.Id, paid.ManagerId, sales.Id, adminId,
            accepted.Value!.HandoverAt!.Value, inverter.Id);
    }

    /// <summary>Local time on a date some days after the installation day (Tuesday <see cref="Day"/>).</summary>
    public static DateTimeOffset Later(int days, int hour = 9) => At(hour, 0, Day.AddDays(days));

    // ------------------------------------------------------------------ common steps

    public async Task<ServiceRequestView> FaultAsync(HandedOver h, string description = "Inverter shows error 2001", Guid? assetId = null,
        string? disasterType = null, DateTimeOffset? incidentAt = null)
    {
        var created = await Customer().CreateFaultAsync(h.CustomerId, new CreateFaultInput(h.ProjectId, description, assetId, null, null, disasterType, incidentAt), None);
        Assert.True(created.Succeeded, created.Message);
        return created.Value!;
    }

    /// <summary>Manager schedules the visit the request needs now (default length unless <paramref name="endsAt"/>), the technician accepts and starts it on site and adds a photo.</summary>
    public async Task<Guid> VisitStartedAsync(HandedOver h, Guid requestId, DateTimeOffset startsAt, DateTimeOffset? endsAt = null)
    {
        var request = (await Manager().GetAsync(requestId, None)).Value!;
        var scheduled = await Manager().ScheduleVisitAsync(h.ManagerId, requestId, new ScheduleVisitInput(h.TechnicianId, startsAt, endsAt, null, request.Revision), None);
        Assert.True(scheduled.Succeeded, scheduled.Message + " " + System.Text.Json.JsonSerializer.Serialize(scheduled.Details));
        var visitId = scheduled.Value!.Visits.Single(v => VisitStatuses.IsOpen(v.Status)).VisitId;
        if (scheduled.Value.Visits.Single(v => v.VisitId == visitId).CustomerConfirmationRequired)
            Assert.True((await Customer().ConfirmAppointmentAsync(h.CustomerId, requestId, None)).Succeeded);
        Assert.True((await Technician().RespondAsync(h.TechnicianId, visitId, true, null, None)).Succeeded);
        Clock.Now = startsAt.ToUniversalTime();
        var started = await Technician().StartAsync(h.TechnicianId, visitId, None);
        Assert.True(started.Succeeded, started.Message);
        var photo = await Installation.VerifiedFileAsync(h.TechnicianId, FilePurposes.ServiceEvidence);
        Assert.True((await Technician().AddEvidenceAsync(h.TechnicianId, visitId, photo, "on site", None)).Succeeded);
        return visitId;
    }

    /// <summary>Inspection visit with the given diagnosed items (AWAITING_DECISION), returning the item ids in order.</summary>
    public async Task<IReadOnlyList<Guid>> InspectedAsync(HandedOver h, Guid requestId, DateTimeOffset at, params DiagnosedItemInput[] items)
    {
        var visit = await VisitStartedAsync(h, requestId, at);
        var reported = await Technician().ReportInspectionAsync(h.TechnicianId, visit, "Checked inverter and strings", false, items, stayOnSite: false, None);
        Assert.True(reported.Succeeded, reported.Message);
        return (await Data().ListItemsAsync(requestId, false, None)).OrderBy(i => i.CreatedAt).ThenBy(i => i.Description).Select(i => i.Id).ToList();
    }

    public async Task DecideAsync(HandedOver h, Guid requestId, params (Guid Item, string Coverage)[] decisions)
    {
        var decided = await Manager().DecideCoverageAsync(h.ManagerId, requestId, decisions.Select(d => new CoverageDecision(d.Item, d.Coverage, "reviewed")).ToList(), None);
        Assert.True(decided.Succeeded, decided.Message);
    }

    /// <summary>The Sales user claims the open quotation task of the request (shared queue).</summary>
    public async Task ClaimQuotationTaskAsync(HandedOver h, Guid requestId)
    {
        var task = (await Tasks().ListOpenAsync(None)).Single(t => t.RequestId == requestId);
        Assert.True((await Tasks().ClaimAsync(h.SalesId, task.TaskId, None)).Succeeded);
    }

    public async Task<ServiceQuotationView> IssueAsync(HandedOver h, Guid requestId, params QuotationLineInput[] lines)
    {
        var issued = await Quotations().IssueAsync(h.SalesId, requestId, new IssueQuotationInput(lines, null, null), None);
        Assert.True(issued.Succeeded, issued.Message);
        return issued.Value!.Quotations.Single(q => QuotationStatuses.IsOpen(q.Status));
    }

    /// <summary>Customer pays a checkout; PayOS reports the transaction at the current clock time (or <paramref name="transactionAt"/>).</summary>
    public async Task<PaymentAttempt?> PayAsync(PaymentAttemptView checkout, DateTimeOffset? transactionAt = null, string? reference = null)
    {
        var body = Installation.PayOs.Webhook(checkout.OrderCode, (long)checkout.Amount, reference ?? $"FT-{checkout.OrderCode}", transactionAt: transactionAt ?? Clock.GetUtcNow());
        await Payments().HandleWebhookAsync(body, None);
        Db.ChangeTracker.Clear();
        return await Db.PaymentAttempts.AsNoTracking().SingleOrDefaultAsync(a => a.OrderCode == checkout.OrderCode);
    }

    public async Task<ServicePackage> PackageAsync(Guid adminId, string kind, decimal price = 2_000_000m, string? visitMonths = "6,12",
        string coveredTypes = "STORM,FLOOD", decimal percent = 70m, decimal perIncident = 5_000_000m, decimal termLimit = 8_000_000m)
    {
        var input = kind == ServicePackageKinds.Maintenance
            ? new PackageInput(kind, "Care 12", "Two visits a year", price, 12, AfterSalesRules.ParseMonths(visitMonths), null, null, null, null, true)
            : new PackageInput(kind, "Storm shield", "Disaster support", price, 12, null, AfterSalesRules.ParseList(coveredTypes), percent, perIncident, termLimit, true);
        var created = await Packages().CreatePackageAsync(adminId, input, None);
        Assert.True(created.Succeeded, created.Message);
        return (await Db.ServicePackages.AsNoTracking().SingleAsync(p => p.Id == created.Value!.PackageId));
    }

    /// <summary>Buys one package for a PAID, handed-over contract and pays it now: the term starts at once.</summary>
    public async Task<ServiceSubscription> ActiveTermAsync(HandedOver h, ServicePackage package)
    {
        var input = package.Kind == ServicePackageKinds.Maintenance
            ? new PurchaseInput(h.ContractId, package.Id, null, false)
            : new PurchaseInput(h.ContractId, null, package.Id, false);
        var bought = await Packages().PurchaseAsync(h.CustomerId, input, None);
        Assert.True(bought.Succeeded, bought.Message);
        await PayAsync(bought.Value!.Checkout);
        return await Db.ServiceSubscriptions.AsNoTracking().SingleAsync(s => s.Id == bought.Value.SubscriptionIds[0]);
    }

    public ValueTask DisposeAsync() => Installation.DisposeAsync();
}
