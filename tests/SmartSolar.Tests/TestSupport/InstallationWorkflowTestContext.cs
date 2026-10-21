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

/// <summary>A survey request that went through all of Flow 1 (booked, accepted by a technician of the area, claimed by Sales).</summary>
public sealed record ConfirmedSurvey(SeededSurveyRequest Request, UserAccount Technician, UserAccount Sales, Guid AppointmentId);

/// <summary>
/// Installation Flow handlers over the SQLite test database (EnsureCreated, so the Installation Flow tables exist without a
/// migration), with the same fixed clock and synthetic geography as the scheduling tests. Files are
/// "uploaded" into a synthetic object store and verified through the real <see cref="FileService"/>.
/// </summary>
public sealed class InstallationWorkflowTestContext : IAsyncDisposable
{
    public const string Bucket = "smartsolar-test";

    private InstallationWorkflowTestContext(SchedulingTestContext scheduling)
    {
        Scheduling = scheduling;
        // As in production with Installation Flow enabled: every scheduling handler sees installation assignments as busy.
        Scheduling.IncludeInstallationAssignments = true;
    }

    public static InstallationWorkflowTestContext Create() => new(SchedulingTestContext.Create());

    /// <summary>Over an existing context, e.g. a disposable PostgreSQL database with the Installation Flow tables.</summary>
    public static InstallationWorkflowTestContext CreateOn(AppDbContext db) => new(SchedulingTestContext.CreateOn(db));

    public SchedulingTestContext Scheduling { get; }
    public AppDbContext Db => Scheduling.Db;
    public FixedTimeProvider Clock => Scheduling.Clock;
    public FakeStorageObjectVerifier Storage { get; } = new();
    public StorageOptions StorageOptions { get; } = new() { DefaultBucket = Bucket };

    public FileService Files(AppDbContext? db = null) => new(new FileStore(db ?? Db), Storage, StorageOptions, Clock, NullLogger<FileService>.Instance);
    public OnSiteSurveyHandlers OnSite() => new(new OnSiteSurveyUnitOfWork(Db), Files(), Scheduling.OnSiteOptions, Scheduling.Policies, Clock);
    public SmartSolar.Modules.Quotations.QuotationHandlers Quotations(AppDbContext? db = null) => new(new QuotationUnitOfWork(db ?? Db), Files(db), new SmartSolar.Modules.Quotations.QuotationOptions(), Clock);

    public SmartSolar.Modules.CustomerContracts.ContractHandlers Contracts(AppDbContext? db = null) => new(new ContractUnitOfWork(db ?? Db), Files(db), Clock);
    public FakePayOsServer PayOs { get; } = new();
    public SmartSolar.Modules.Payments.PaymentOptions PaymentOptions { get; } = new() { ReturnUrl = "https://app.test/payment/return", CancelUrl = "https://app.test/payment/cancel" };
    public SmartSolar.Modules.Payments.PaymentService Payments(AppDbContext? db = null) => new(new PaymentUnitOfWork(db ?? Db), PayOs.Gateway(), PaymentOptions, Clock,
        NullLogger<SmartSolar.Modules.Payments.PaymentService>.Instance);

    /// <summary>Bootstrap defaults of installation.* (shared with the scheduling context's settings provider).</summary>
    public SmartSolar.Modules.Installations.InstallationOptions InstallationOptions => Scheduling.InstallationOptions;

    /// <summary>Scheduling unit of work with current installation assignments in every day state (Installation Flow enabled).</summary>
    public SchedulingUnitOfWork SchedulingWithInstallations(AppDbContext? db = null) => new(db ?? Db, includeInstallationAssignments: true, Clock);

    public SmartSolar.Modules.Installations.InstallationHandlers Installations(AppDbContext? db = null)
    {
        var scheduling = SchedulingWithInstallations(db);
        var planner = new SmartSolar.Modules.Installations.InstallationSchedulePlanner(Scheduling.Eligibility(scheduling), Scheduling.Calendar, InstallationOptions,
            Scheduling.Assignments(scheduling));
        return new(new InstallationUnitOfWork(db ?? Db), scheduling, planner, Files(db), Scheduling.Calendar, InstallationOptions, Clock);
    }

    /// <summary>Survey self-acceptance over <paramref name="db"/> that sees installation assignments.</summary>
    public SmartSolar.Modules.Scheduling.PreSurveyAppointments.TechnicianJobHandlers JobsWithInstallations(AppDbContext? db = null)
    {
        var u = SchedulingWithInstallations(db);
        return new(u, Scheduling.Assignments(u), Scheduling.Eligibility(u), Scheduling.Calendar, Clock);
    }

    /// <summary>Survey bookings that see installation assignments (as production does once Installation Flow is enabled).</summary>
    public SmartSolar.Modules.Scheduling.PreSurveyAppointments.BookSurveyAppointmentHandler BookWithInstallations()
    {
        var u = SchedulingWithInstallations();
        return new(u, Scheduling.Reservations(u), Scheduling.Eligibility(u), Scheduling.Coverage(u), Scheduling.Calendar, Clock);
    }

    /// <summary>Flow 1 plus a completed on-site survey: ready for a quotation.</summary>
    public async Task<ConfirmedSurvey> QuotableSurveyAsync(string customer = "Customer")
    {
        var survey = await ConfirmedSurveyAsync(customer);
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

    /// <summary>
    /// Flow 1 end to end: a technician of Binh Tan self-accepts a 10:00 booking on <see cref="Day"/>, then Sales
    /// claims the request (ownership after scheduling).
    /// </summary>
    public async Task<ConfirmedSurvey> ConfirmedSurveyAsync(string customer = "Customer")
    {
        var technician = await Scheduling.SeedTechnicianAsync($"Tech for {customer}", BinhTan);
        var request = await Scheduling.SeedSurveyRequestAsync(BinhTan, customer);
        var booked = await Scheduling.Book().HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(10), null), CancellationToken.None);
        Assert.True(booked.Succeeded, booked.Message);
        var accepted = await Scheduling.AcceptAsync(technician.Id, booked.Value!.AppointmentId);
        Assert.True(accepted.Succeeded, accepted.Message);
        var sales = await Scheduling.ClaimAsync(request);
        return new ConfirmedSurvey(request, technician, sales, booked.Value.AppointmentId);
    }

    /// <summary>
    /// The Admin setup every new contract needs: an active default INSTALLATION warranty option (120 months), unless a test already
    /// configured its own options.
    /// </summary>
    public async Task EnsureInstallationWarrantyOptionAsync()
    {
        if (await Db.WarrantyOptions.AnyAsync(o => o.Kind == SmartSolar.Modules.AfterSales.WarrantyOptionKinds.Installation && o.IsActive)) return;
        Db.WarrantyOptions.Add(new SmartSolar.Modules.AfterSales.WarrantyOption
        {
            Id = Guid.NewGuid(), Kind = SmartSolar.Modules.AfterSales.WarrantyOptionKinds.Installation, Months = 120, IsActive = true, IsDefault = true,
            CreatedAt = Clock.GetUtcNow(), UpdatedAt = Clock.GetUtcNow(),
        });
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
    }

    /// <summary>Registers a file while storage is unreachable, so it stays PENDING_VERIFICATION.</summary>
    public async Task<Guid> UnverifiedFileAsync(Guid uploader, string purpose)
    {
        var policy = FilePolicies.ByPurpose[purpose];
        var key = $"{policy.KeyPrefix}{uploader}/{Guid.NewGuid():N}.jpg";
        Storage.Put(Bucket, key, FakeStorageObjectVerifier.JpegBytes);
        Storage.Unavailable = true;
        try
        {
            await Files().RegisterAsync(new RegisterFileCommand(uploader, purpose, null, key, "file.jpg", FilePolicies.Jpeg, FakeStorageObjectVerifier.JpegBytes.Length, null),
                CancellationToken.None);
        }
        finally
        {
            Storage.Unavailable = false;
        }
        var file = await Db.StoredFiles.AsNoTracking().SingleAsync(f => f.ObjectKey == key);
        Assert.Equal(FileStatuses.PendingVerification, file.Status);
        return file.Id;
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
