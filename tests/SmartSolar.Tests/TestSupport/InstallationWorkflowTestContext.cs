using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.InstallationWorkflow;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.TestSupport;

/// <summary>A survey request that went through all of Flow 1 (booked, claimed, assigned, acknowledged).</summary>
public sealed record AcknowledgedSurvey(SeededSurveyRequest Request, UserAccount Technician, UserAccount Sales, Guid AppointmentId);

/// <summary>
/// Installation Flow handlers over the SQLite test database (EnsureCreated, so the Installation Flow tables exist without a
/// migration), with the same fixed clock and synthetic travel as the scheduling tests. Files are
/// "uploaded" into a synthetic object store and verified through the real <see cref="FileService"/>.
/// </summary>
public sealed class InstallationWorkflowTestContext : IAsyncDisposable
{
    public const string Bucket = "smartsolar-test";

    private InstallationWorkflowTestContext(SchedulingTestContext scheduling) => Scheduling = scheduling;

    public static InstallationWorkflowTestContext Create() => new(SchedulingTestContext.Create());

    /// <summary>Over an existing context, e.g. a disposable PostgreSQL database with the Installation Flow tables.</summary>
    public static InstallationWorkflowTestContext CreateOn(AppDbContext db) => new(SchedulingTestContext.CreateOn(db));

    public SchedulingTestContext Scheduling { get; }
    public AppDbContext Db => Scheduling.Db;
    public FixedTimeProvider Clock => Scheduling.Clock;
    public FakeStorageObjectVerifier Storage { get; } = new();
    public StorageOptions StorageOptions { get; } = new() { DefaultBucket = Bucket };

    public FileService Files(AppDbContext? db = null) => new(new FileStore(db ?? Db), Storage, StorageOptions, Clock, NullLogger<FileService>.Instance);
    public OnSiteSurveyHandlers OnSite() => new(new OnSiteSurveyUnitOfWork(Db), Files(), new OnSiteSurveyOptions(), Clock);
    public SmartSolar.Modules.Quotations.QuotationHandlers Quotations(AppDbContext? db = null) => new(new QuotationUnitOfWork(db ?? Db), Files(db), new SmartSolar.Modules.Quotations.QuotationOptions(), Clock);

    public SmartSolar.Modules.CustomerContracts.ContractHandlers Contracts(AppDbContext? db = null) => new(new ContractUnitOfWork(db ?? Db), Files(db), Clock);
    public FakePayOsServer PayOs { get; } = new();
    public SmartSolar.Modules.Payments.PaymentOptions PaymentOptions { get; } = new() { ReturnUrl = "https://app.test/payment/return", CancelUrl = "https://app.test/payment/cancel" };
    public SmartSolar.Modules.Payments.PaymentService Payments(AppDbContext? db = null) => new(new PaymentUnitOfWork(db ?? Db), PayOs.Gateway(), PaymentOptions, Clock,
        NullLogger<SmartSolar.Modules.Payments.PaymentService>.Instance);

    public SmartSolar.Modules.Installations.InstallationOptions InstallationOptions { get; } = new();

    /// <summary>Scheduling unit of work with installation shifts in every day state (Installation Flow enabled).</summary>
    public SchedulingUnitOfWork SchedulingWithInstallations(AppDbContext? db = null) => new(db ?? Db, includeInstallationShifts: true);

    public SmartSolar.Modules.Installations.InstallationHandlers Installations(AppDbContext? db = null)
    {
        var scheduling = SchedulingWithInstallations(db);
        var planner = new SmartSolar.Modules.Installations.InstallationSchedulePlanner(scheduling, Scheduling.Travel, Scheduling.Calendar, Scheduling.Options, Clock);
        return new(new InstallationUnitOfWork(db ?? Db), scheduling, planner, Files(db), Scheduling.Calendar, InstallationOptions, Clock);
    }

    /// <summary>Survey bookings that see installation shifts (as production does once Installation Flow is enabled).</summary>
    public SmartSolar.Modules.Scheduling.PreSurveyAppointments.BookSurveyAppointmentHandler BookWithInstallations()
    {
        var u = SchedulingWithInstallations();
        var evaluator = new SmartSolar.Modules.Scheduling.Services.TechnicianAvailabilityEvaluator(Scheduling.Options, Clock);
        return new(u, new SmartSolar.Modules.Scheduling.Services.AppointmentReservationService(u, evaluator, Scheduling.Travel, Scheduling.Options, Clock, Scheduling.Calendar),
            Scheduling.Calendar, Clock);
    }

    /// <summary>Flow 1 plus a completed on-site survey: ready for a quotation.</summary>
    public async Task<AcknowledgedSurvey> QuotableSurveyAsync(string customer = "Customer")
    {
        var survey = await AcknowledgedSurveyAsync(customer);
        await SmartSolar.Tests.Unit.InstallationWorkflow.OnSiteSurveyTests.CompleteAsync(this, survey);
        return survey;
    }

    public async Task<SmartSolar.Modules.Catalog.Entities.Product> ProductAsync(string sku, decimal price)
    {
        var product = PreSurveyTestContext.NewPanelProduct(sku);
        product.UnitPrice = price;
        Db.Products.Add(product);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return product;
    }

    /// <summary>Flow 1 end to end: technician at District 1, customer at Binh Tan, 10:00 on <see cref="Day"/>.</summary>
    public async Task<AcknowledgedSurvey> AcknowledgedSurveyAsync(string customer = "Customer")
    {
        Scheduling.Travel.Minutes = _ => 20;
        var technician = await Scheduling.SeedTechnicianAsync($"Tech for {customer}", District1);
        var request = await Scheduling.SeedSurveyRequestAsync(BinhTan, customer);
        var booked = await Scheduling.Book().HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(10), null), CancellationToken.None);
        Assert.True(booked.Succeeded, booked.Message);
        var sales = await Scheduling.ClaimAsync(request);
        var appointment = await Scheduling.ActiveAppointmentAsync(request.Request.Id);
        var assigned = await Scheduling.Assign().HandleAsync(new AssignTechnicianCommand(sales.Id, request.Request.Id, appointment.ReservedTechnicianUserId), CancellationToken.None);
        Assert.True(assigned.Succeeded, assigned.Message);
        var ack = await Scheduling.Jobs().AcknowledgeAsync(appointment.ReservedTechnicianUserId, appointment.Id, CancellationToken.None);
        Assert.True(ack.Succeeded, ack.Message);
        var tech = await Db.UserAccounts.AsNoTracking().SingleAsync(u => u.Id == appointment.ReservedTechnicianUserId);
        return new AcknowledgedSurvey(request, tech, sales, appointment.Id);
    }

    /// <summary>Puts synthetic bytes into the object store and registers + verifies them for the uploader.</summary>
    public async Task<Guid> VerifiedFileAsync(Guid uploader, string purpose, string extension = "jpg")
    {
        var policy = FilePolicies.ByPurpose[purpose];
        var (bytes, type) = extension switch
        {
            "pdf" => (FakeStorageObjectVerifier.PdfBytes, FilePolicies.Pdf),
            "png" => (FakeStorageObjectVerifier.PngBytes, FilePolicies.Png),
            _ => (FakeStorageObjectVerifier.JpegBytes, FilePolicies.Jpeg),
        };
        var key = $"{policy.KeyPrefix}{uploader}/{Guid.NewGuid():N}.{extension}";
        Storage.Put(Bucket, key, bytes);
        var result = await Files().RegisterAsync(new RegisterFileCommand(uploader, purpose, null, key, $"file.{extension}", type, bytes.Length, null), CancellationToken.None);
        Assert.True(result.Succeeded && result.Value!.Status == FileStatuses.Verified, result.Message ?? result.Value?.RejectionReason);
        return result.Value!.FileId;
    }

    public ValueTask DisposeAsync() => Scheduling.DisposeAsync();
}
